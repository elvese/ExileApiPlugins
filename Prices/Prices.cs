using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared;
using Newtonsoft.Json;
using SharpDX;

namespace Prices
{
    /// <summary>
    /// Reprices Chaos Orb listings in every tab of the open Merchant Shop panel.
    ///
    /// Merchant prices are only materialised in the UI after an item is hovered, so the plugin has
    /// to walk the mouse over the shop and use the Set Item Price dialog. It never writes a price
    /// until that exact dialog has been found and validated.
    /// </summary>
    public class PricesCore : BaseSettingsPlugin<PricesSettings>
    {
        private const string CoroutineName = "Prices - Merchant Shop repricing";
        private const string ChaosOrbName = "Chaos Orb";
        private const string PriceDialogTitle = "Set Item Price";
        private const string StateFileName = "PricesData.json";
        private const string LockedNotice = "cannot modify or remove";
        private const int UiPollMilliseconds = 15;
        private const int DialogTimeoutMilliseconds = 700;
        private const int PanelBlinkMilliseconds = 800;

        private static readonly TimeSpan RepriceInterval = TimeSpan.FromMinutes(5);
        private static readonly int[] ShopTabBarPath = {2, 0, 0, 1, 1, 0, 0, 1};
        private const int ShopGridsIndex = 1;
        private const int ShopDropdownButtonIndex = 2;
        private const int ShopDropdownListIndex = 4;
        private const int ShopDropdownRowsIndex = 2;

        private readonly Dictionary<string, PriceState> _priceStates =
            new Dictionary<string, PriceState>(StringComparer.Ordinal);

        private Coroutine _worker;
        private DateTime _nextRunUtc;
        private bool _abortRun;
        private bool _stateDirty;
        private int _changed;
        private int _atMinimum;
        private int _skippedOtherCurrency;
        private int _skippedLocked;
        private int _unreadable;
        private string _lastSummary = "jeszcze nie uruchomiono";
        private Vector2 _cursorBeforeRun;

        public PricesCore()
        {
            Name = "Prices";
            Description = "Co 5 minut obniża ceny Chaos Orb w Merchant Shop o 10%, maksymalnie do 30% ceny początkowej.";
        }

        public override bool Initialise()
        {
            LoadState();
            _nextRunUtc = DateTime.UtcNow.Add(RepriceInterval);

            Input.RegisterKey(Settings.RunNowHotkey.Value);
            Settings.RunNowHotkey.OnValueChanged += () => Input.RegisterKey(Settings.RunNowHotkey.Value);
            return true;
        }

        public override Job Tick()
        {
            if (_worker != null && _worker.IsDone)
                _worker = null;

            if (Settings.RunNowHotkey.PressedOnce())
                TryStartRun(true);

            if (Settings.AutomaticRepricing.Value && DateTime.UtcNow >= _nextRunUtc)
                TryStartRun(false);

            return null;
        }

        public override void Render()
        {
            if (!Settings.ShowStatus.Value || !IsMerchantPanelVisible())
                return;

            var status = _worker != null && !_worker.IsDone
                ? "Prices: trwa zmiana cen..."
                : "Prices: następny przebieg za " + FormatRemainingTime() + " | " + _lastSummary;

            Graphics.DrawText(status, new Vector2(10, 100), Color.LightGreen);
        }

        public override void OnPluginDestroyForHotReload()
        {
            StopWorker();
            SaveState();
        }

        public override void OnClose()
        {
            StopWorker();
            SaveState();
            base.OnClose();
        }

        private void TryStartRun(bool manual)
        {
            if (_worker != null && !_worker.IsDone)
                return;

            if (!GameController.Window.IsForeground())
            {
                if (manual)
                    LogError("Prices: okno gry musi być aktywne.", 5);
                return;
            }

            if (!IsMerchantPanelVisible())
            {
                if (manual)
                    LogError("Prices: otwórz panel Merchant Shop przed uruchomieniem.", 5);
                return;
            }

            // Start-to-start cadence. If a very large shop takes more than five minutes, another
            // run starts as soon as this one has safely finished, never concurrently.
            _nextRunUtc = DateTime.UtcNow.Add(RepriceInterval);
            _worker = new Coroutine(RepriceAllShopTabs(), this, CoroutineName);
            Core.ParallelRunner.Run(_worker);
        }

        private IEnumerator RepriceAllShopTabs()
        {
            ResetRunCounters();
            _cursorBeforeRun = Input.ForceMousePosition;

            var panel = GetMerchantPanel();
            if (panel == null || !SafeIsVisible(panel))
            {
                _abortRun = true;
                FinishRun();
                yield break;
            }

            var grids = GetShopGrids(panel);
            var tabCount = grids == null ? 0 : SafeChildCount(grids);
            var startingTab = CurrentShopTab(grids);

            if (tabCount <= 0)
            {
                // Keeping a current-tab fallback makes the plugin useful after a harmless tab-bar
                // layout change, but it reports that it could not prove every tab was visited.
                LogError("Prices: nie znaleziono listy zakładek Merchant Shop; przetwarzam tylko widoczną zakładkę.", 8);
                yield return ProcessCurrentTab(startingTab < 0 ? 0 : startingTab);
            }
            else
            {
                for (var tab = 0; tab < tabCount && !_abortRun; tab++)
                {
                    if (CancellationRequested())
                    {
                        _abortRun = true;
                        break;
                    }

                    yield return WaitForPanel();
                    if (_abortRun)
                        break;

                    yield return SelectShopTab(tab);
                    if (_abortRun)
                        break;

                    if (CurrentShopTab(grids) != tab)
                    {
                        LogError("Prices: nie udało się otworzyć zakładki Merchant Shop " + (tab + 1) + ".", 5);
                        continue;
                    }

                    yield return ProcessCurrentTab(tab);
                }

                if (!_abortRun && startingTab >= 0 && startingTab < tabCount)
                    yield return SelectShopTab(startingTab);
            }

            yield return RestoreCursor();
            FinishRun();
        }

        private IEnumerator ProcessCurrentTab(int tabIndex)
        {
            var snapshot = GetCurrentMerchantItems();
            var targets = new List<MerchantItemTarget>();

            foreach (var item in snapshot)
            {
                var target = MerchantItemTarget.From(tabIndex, item);
                if (target != null)
                    targets.Add(target);
            }

            foreach (var target in targets)
            {
                if (_abortRun || CancellationRequested())
                {
                    _abortRun = true;
                    yield break;
                }

                yield return WaitForPanel();
                if (_abortRun)
                    yield break;

                if (IsChatInputOpen())
                {
                    LogError("Prices: przerwano, ponieważ pole czatu jest otwarte. Nic nie zostało wpisane.", 8);
                    _abortRun = true;
                    yield break;
                }

                var item = FindCurrentItem(target);
                if (item == null)
                {
                    _unreadable++;
                    continue;
                }

                MoveToItem(item);
                yield return new WaitTime(Settings.ActionDelay.Value);

                yield return WaitForPrice(item, 650);

                // Iterator methods cannot have out parameters, so the yielded tooltip wait carries
                // its result through two short-lived fields which are cleared after every read.
                var currentPrice = _lastReadPrice;
                var currency = _lastReadCurrency;
                ClearLastReadPrice();

                if (currentPrice <= 0 || string.IsNullOrWhiteSpace(currency))
                {
                    _unreadable++;
                    continue;
                }

                if (!string.Equals(currency.Trim(), ChaosOrbName, StringComparison.OrdinalIgnoreCase))
                {
                    _skippedOtherCurrency++;
                    continue;
                }

                var state = GetOrCreateState(target, currentPrice);
                state.LastSeenUtc = DateTime.UtcNow;
                state.LastObservedPrice = currentPrice;
                _stateDirty = true;

                // A manual raise above the old starting price begins a new pricing cycle. A manual
                // cut below the floor is never raised by the plugin.
                if (currentPrice > state.InitialPrice)
                    state.InitialPrice = currentPrice;

                var nextPrice = PriceCalculator.GetNextPrice(state.InitialPrice, currentPrice);
                if (nextPrice >= currentPrice)
                {
                    _atMinimum++;
                    continue;
                }

                if (TooltipContains(item.Tooltip, LockedNotice))
                {
                    _skippedLocked++;
                    continue;
                }

                yield return SetItemPrice(item, target, nextPrice);
            }
        }

        // Iterator methods cannot expose out parameters. These two fields are cleared around every
        // read and only exist to carry one result across a yielded tooltip wait.
        private int _lastReadPrice;
        private string _lastReadCurrency;

        private IEnumerator WaitForPrice(NormalInventoryItem item, int timeoutMilliseconds)
        {
            ClearLastReadPrice();
            var timer = Stopwatch.StartNew();

            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                int price;
                string currency;
                if (TryReadMerchantPrice(item.Tooltip, out price, out currency))
                {
                    _lastReadPrice = price;
                    _lastReadCurrency = currency;
                    yield break;
                }

                yield return new WaitTime(UiPollMilliseconds);
            }
        }

        private void ClearLastReadPrice()
        {
            _lastReadPrice = 0;
            _lastReadCurrency = null;
        }

        private IEnumerator SetItemPrice(NormalInventoryItem item, MerchantItemTarget target, int newPrice)
        {
            Input.Click(MouseButtons.Right);
            yield return new WaitTime(20);

            Element dialog = null;
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < DialogTimeoutMilliseconds)
            {
                dialog = GetPriceDialog();
                if (dialog != null)
                    break;
                yield return new WaitTime(UiPollMilliseconds);
            }

            if (dialog == null)
            {
                var unexpectedPopup = GetVisiblePopupTitle();
                if (!string.IsNullOrWhiteSpace(unexpectedPopup))
                {
                    LogError("Prices: otwarto nieoczekiwane okno „" + unexpectedPopup + "”. Przerywam bez wpisywania ceny.", 8);
                    _abortRun = true;
                }
                else
                {
                    _skippedLocked++;
                }
                yield break;
            }

            if (IsChatInputOpen())
            {
                LogError("Prices: pole czatu otworzyło się podczas edycji. Przerwano bez wpisywania.", 8);
                _abortRun = true;
                yield break;
            }

            var amountInput = Navigate(dialog, 2, 0, 0);
            if (amountInput == null)
            {
                CancelPriceDialog();
                LogError("Prices: nie znaleziono pola kwoty w oknie Set Item Price.", 8);
                _abortRun = true;
                yield break;
            }

            ClickElement(amountInput);
            yield return new WaitTime(25);

            Input.KeyDown(Keys.LControlKey);
            PressKey(Keys.A);
            Input.KeyUp(Keys.LControlKey);
            yield return new WaitTime(15);

            yield return TypeNumber(newPrice);
            yield return new WaitTime(25);

            var amountReadBack = ReadAmountValue(amountInput);
            if (amountReadBack.HasValue && amountReadBack.Value != newPrice)
            {
                CancelPriceDialog();
                LogError("Prices: pole ceny zawiera " + amountReadBack.Value + " zamiast " + newPrice + ". Nie zatwierdzam.", 8);
                _abortRun = true;
                yield break;
            }

            // Enter is only sent while the verified Set Item Price dialog still owns input.
            if (GetPriceDialog() == null || IsChatInputOpen())
            {
                LogError("Prices: okno ceny utraciło fokus; Enter nie został wysłany.", 8);
                _abortRun = true;
                yield break;
            }

            PressKey(Keys.Enter);

            timer.Restart();
            while (timer.ElapsedMilliseconds < 2000 && GetPriceDialog() != null)
                yield return new WaitTime(UiPollMilliseconds);

            if (GetPriceDialog() != null)
            {
                CancelPriceDialog();
                LogError("Prices: okno ceny nie zamknęło się po zatwierdzeniu.", 8);
                _abortRun = true;
                yield break;
            }

            _changed++;
            PriceState state;
            if (_priceStates.TryGetValue(target.Key, out state))
            {
                state.LastObservedPrice = newPrice;
                state.LastReducedUtc = DateTime.UtcNow;
                _stateDirty = true;
            }

            yield return new WaitTime(Settings.ActionDelay.Value);
        }

        private IEnumerator SelectShopTab(int tabIndex)
        {
            var panel = GetMerchantPanel();
            var tabBar = Navigate(panel, ShopTabBarPath);
            var grids = Navigate(tabBar, ShopGridsIndex);
            if (panel == null || tabBar == null || grids == null)
            {
                _abortRun = true;
                yield break;
            }

            if (CurrentShopTab(grids) == tabIndex)
            {
                yield return WaitForStableItems();
                yield break;
            }

            var dropdown = Navigate(tabBar, ShopDropdownListIndex);
            if (dropdown == null)
            {
                _abortRun = true;
                yield break;
            }

            if (!dropdown.IsVisibleLocal)
            {
                var toggle = Navigate(tabBar, ShopDropdownButtonIndex);
                if (toggle == null)
                {
                    _abortRun = true;
                    yield break;
                }

                ClickElement(toggle);
                var openTimer = Stopwatch.StartNew();
                while (openTimer.ElapsedMilliseconds < 1500 && !dropdown.IsVisibleLocal)
                    yield return new WaitTime(UiPollMilliseconds);

                if (!dropdown.IsVisibleLocal)
                {
                    _abortRun = true;
                    yield break;
                }
            }

            var row = Navigate(tabBar, ShopDropdownListIndex, ShopDropdownRowsIndex, tabIndex);
            if (row == null)
            {
                _abortRun = true;
                yield break;
            }

            ClickElement(row);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 3000 && CurrentShopTab(grids) != tabIndex)
                yield return new WaitTime(UiPollMilliseconds);

            if (CurrentShopTab(grids) != tabIndex)
                yield break;

            yield return WaitForStableItems();
        }

        private IEnumerator WaitForStableItems()
        {
            var timer = Stopwatch.StartNew();
            var lastCount = -1;
            var stableSince = 0L;

            while (timer.ElapsedMilliseconds < 1500)
            {
                var count = GetCurrentMerchantItems().Count;
                if (count != lastCount)
                {
                    lastCount = count;
                    stableSince = timer.ElapsedMilliseconds;
                }
                else if (count > 0 && timer.ElapsedMilliseconds - stableSince >= 50)
                {
                    yield break;
                }
                else if (count == 0 && timer.ElapsedMilliseconds >= 400)
                {
                    yield break;
                }

                yield return new WaitTime(UiPollMilliseconds);
            }
        }

        private IEnumerator WaitForPanel()
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < PanelBlinkMilliseconds)
            {
                if (IsMerchantPanelVisible())
                    yield break;
                yield return new WaitTime(UiPollMilliseconds);
            }

            LogError("Prices: panel Merchant Shop został zamknięty.", 5);
            _abortRun = true;
        }

        private IEnumerator RestoreCursor()
        {
            if (GameController.Window.IsForeground())
                yield return Input.SetCursorPositionSmooth(_cursorBeforeRun);
        }

        private IEnumerator TypeNumber(int value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            foreach (var character in text)
            {
                var key = (Keys) ((int) Keys.D0 + character - '0');
                PressKey(key);
                yield return new WaitTime(10);
            }
        }

        private static void PressKey(Keys key)
        {
            Input.KeyDown(key);
            Input.KeyUp(key);
        }

        private void MoveToItem(NormalInventoryItem item)
        {
            var rect = item.GetClientRect();
            var window = GameController.Window.GetWindowRectangle().TopLeft;
            Input.SetCursorPos(new Vector2(window.X + rect.X + Math.Min(5f, rect.Width / 2f),
                window.Y + rect.Y + Math.Min(5f, rect.Height / 2f)));
        }

        private void ClickElement(Element element)
        {
            var rect = element.GetClientRect();
            var window = GameController.Window.GetWindowRectangle().TopLeft;
            Input.SetCursorPos(new Vector2(window.X + rect.X + rect.Width / 2f,
                window.Y + rect.Y + rect.Height / 2f));
            Input.Click(MouseButtons.Left);
        }

        private void CancelPriceDialog()
        {
            if (GetPriceDialog() != null)
                PressKey(Keys.Escape);
        }

        private static int? ReadAmountValue(Element input)
        {
            var text = ReadElementText(input);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var digits = new string(text.Where(char.IsDigit).ToArray());
            int value;
            return digits.Length > 0 && int.TryParse(digits, out value) ? value : (int?) null;
        }

        private Element GetPriceDialog()
        {
            try
            {
                var popup = GetMember(GameController.Game.IngameState.IngameUi, "PopUpWindow") as Element;
                if (popup == null || !SafeIsVisible(popup))
                    return null;

                var title = ReadElementText(Navigate(popup, 0, 0));
                return string.Equals(title, PriceDialogTitle, StringComparison.OrdinalIgnoreCase) ? popup : null;
            }
            catch
            {
                return null;
            }
        }

        private string GetVisiblePopupTitle()
        {
            try
            {
                var popup = GetMember(GameController.Game.IngameState.IngameUi, "PopUpWindow") as Element;
                return popup != null && SafeIsVisible(popup) ? ReadElementText(Navigate(popup, 0, 0)) : null;
            }
            catch
            {
                return null;
            }
        }

        private Element GetMerchantPanel()
        {
            try
            {
                return GetMember(GameController.Game.IngameState.IngameUi, "OfflineMerchantPanel") as Element;
            }
            catch
            {
                return null;
            }
        }

        private bool IsMerchantPanelVisible()
        {
            return SafeIsVisible(GetMerchantPanel());
        }

        private static bool SafeIsVisible(Element element)
        {
            try
            {
                return element != null && element.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        private bool IsChatInputOpen()
        {
            try
            {
                var chatPanel = GetMember(GameController.Game.IngameState.IngameUi, "ChatPanel");
                var input = GetMember(chatPanel, "ChatInputElement") as Element;
                return input != null && input.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        private Element GetShopGrids(Element panel)
        {
            return Navigate(Navigate(panel, ShopTabBarPath), ShopGridsIndex);
        }

        private static int CurrentShopTab(Element grids)
        {
            if (grids == null)
                return -1;

            var children = SafeChildren(grids);
            for (var index = 0; index < children.Count; index++)
            {
                try
                {
                    if (children[index].IsVisibleLocal)
                        return index;
                }
                catch
                {
                    // The panel may be rebuilding between frames.
                }
            }

            return -1;
        }

        private List<NormalInventoryItem> GetCurrentMerchantItems()
        {
            var result = new List<NormalInventoryItem>();
            try
            {
                var panel = GetMerchantPanel();
                var visibleStash = GetMember(panel, "VisibleStash");
                var items = GetMember(visibleStash, "VisibleInventoryItems") as IEnumerable;
                if (items == null)
                    return result;

                foreach (var item in items)
                {
                    var inventoryItem = item as NormalInventoryItem;
                    if (inventoryItem != null && inventoryItem.Address != 0 && inventoryItem.Item != null)
                        result.Add(inventoryItem);
                }
            }
            catch
            {
                // A commit briefly rebuilds the merchant inventory. The caller will retry it.
            }

            return result;
        }

        private NormalInventoryItem FindCurrentItem(MerchantItemTarget target)
        {
            foreach (var item in GetCurrentMerchantItems())
            {
                try
                {
                    var path = item.Item == null ? null : item.Item.Path;
                    if (target.InventoryId != 0 && item.Item != null &&
                        item.Item.InventoryId == target.InventoryId && string.Equals(path, target.Path, StringComparison.Ordinal))
                        return item;

                    // Some panel rebuilds replace the client-side inventory id. The shop slot and
                    // metadata path still identify the captured listing for the duration of a run.
                    if (item.InventPosX == target.X && item.InventPosY == target.Y &&
                        string.Equals(path, target.Path, StringComparison.Ordinal))
                        return item;
                }
                catch
                {
                    // Ignore one stale element and continue through the current list.
                }
            }

            return null;
        }

        private static bool TryReadMerchantPrice(Element tooltip, out int price, out string currency)
        {
            price = 0;
            currency = null;
            if (tooltip == null || !TooltipContains(tooltip, "Asking Price"))
                return false;

            // Current client layout: tooltip -> body -> rows -> asking-price row -> price group.
            var body = Navigate(tooltip, 0, 1);
            var bodyChildren = SafeChildren(body);
            if (bodyChildren.Count > 0)
            {
                var priceGroup = Navigate(bodyChildren[bodyChildren.Count - 1], 1);
                if (TryParsePriceGroup(priceGroup, out price, out currency))
                    return true;
            }

            // Fallback for harmless layout shifts: the asking-price tooltip is already confirmed,
            // now locate its split "Nx" / icon / "Currency" group.
            var stack = new Stack<Element>();
            stack.Push(tooltip);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 500)
            {
                var element = stack.Pop();
                if (TryParsePriceGroup(element, out price, out currency))
                    return true;

                foreach (var child in SafeChildren(element))
                    stack.Push(child);
            }

            return false;
        }

        private static bool TryParsePriceGroup(Element group, out int price, out string currency)
        {
            price = 0;
            currency = null;
            var children = SafeChildren(group);
            if (children.Count < 3)
                return false;

            var priceText = ReadElementText(children[0]);
            var currencyText = ReadElementText(children[2]);
            if (string.IsNullOrWhiteSpace(priceText) || string.IsNullOrWhiteSpace(currencyText) ||
                !priceText.Trim().EndsWith("x", StringComparison.OrdinalIgnoreCase))
                return false;

            var number = priceText.Replace("x", string.Empty).Replace("X", string.Empty)
                .Replace(",", string.Empty).Trim();
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out price) || price <= 0)
                return false;

            currency = currencyText.Trim();
            return true;
        }

        private static bool TooltipContains(Element tooltip, string fragment)
        {
            if (tooltip == null)
                return false;

            var stack = new Stack<Element>();
            stack.Push(tooltip);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 500)
            {
                var element = stack.Pop();
                var text = ReadElementText(element);
                if (!string.IsNullOrEmpty(text) &&
                    text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                foreach (var child in SafeChildren(element))
                    stack.Push(child);
            }

            return false;
        }

        private static Element Navigate(Element root, params int[] path)
        {
            var current = root;
            foreach (var index in path)
            {
                var children = SafeChildren(current);
                if (index < 0 || index >= children.Count)
                    return null;
                current = children[index];
            }
            return current;
        }

        private static IList<Element> SafeChildren(Element element)
        {
            if (element == null)
                return new List<Element>();
            try
            {
                return element.Children ?? new List<Element>();
            }
            catch
            {
                return new List<Element>();
            }
        }

        private static int SafeChildCount(Element element)
        {
            try
            {
                return element == null ? 0 : (int) element.ChildCount;
            }
            catch
            {
                return 0;
            }
        }

        private static string ReadElementText(Element element)
        {
            if (element == null)
                return null;

            try
            {
                // Newer ExileCore versions expose TextNoTags; reflection keeps this plugin source
                // compatible with the older Core project layout used by this repository.
                var textNoTags = GetMember(element, "TextNoTags") as string;
                var text = textNoTags ?? element.Text;
                return string.IsNullOrWhiteSpace(text) ? null : StripTags(text).Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string StripTags(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0)
                return value;

            var result = new System.Text.StringBuilder(value.Length);
            var inTag = false;
            foreach (var character in value)
            {
                if (character == '<')
                {
                    inTag = true;
                    continue;
                }
                if (character == '>')
                {
                    inTag = false;
                    continue;
                }
                if (!inTag)
                    result.Append(character);
            }
            return result.ToString();
        }

        private static object GetMember(object target, string propertyName)
        {
            if (target == null)
                return null;

            try
            {
                var property = target.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        private PriceState GetOrCreateState(MerchantItemTarget target, int currentPrice)
        {
            PriceState state;
            if (!_priceStates.TryGetValue(target.Key, out state) &&
                !_priceStates.TryGetValue(target.SlotKey, out state))
            {
                state = new PriceState
                {
                    InitialPrice = currentPrice,
                    LastObservedPrice = currentPrice,
                    LastSeenUtc = DateTime.UtcNow
                };
            }

            // InventoryId is preferred because it follows a moved item. The slot alias preserves
            // the floor if the Merchant Shop rebuild assigns that same listing another client id.
            _priceStates[target.Key] = state;
            _priceStates[target.SlotKey] = state;
            _stateDirty = true;
            return state;
        }

        private void LoadState()
        {
            try
            {
                var path = Path.Combine(DirectoryFullName, StateFileName);
                if (!File.Exists(path))
                    return;

                var loaded = JsonConvert.DeserializeObject<Dictionary<string, PriceState>>(File.ReadAllText(path));
                if (loaded == null)
                    return;

                var cutoff = DateTime.UtcNow.AddDays(-30);
                foreach (var pair in loaded)
                {
                    if (pair.Value != null && pair.Value.InitialPrice > 0 && pair.Value.LastSeenUtc >= cutoff)
                        _priceStates[pair.Key] = pair.Value;
                }
            }
            catch (Exception exception)
            {
                LogError("Prices: nie udało się wczytać cen początkowych: " + exception.Message, 8);
            }
        }

        private void SaveState()
        {
            if (!_stateDirty)
                return;

            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-30);
                foreach (var key in _priceStates.Where(x => x.Value.LastSeenUtc < cutoff).Select(x => x.Key).ToList())
                    _priceStates.Remove(key);

                var path = Path.Combine(DirectoryFullName, StateFileName);
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(_priceStates, Formatting.Indented));
                File.Copy(temporaryPath, path, true);
                File.Delete(temporaryPath);
                _stateDirty = false;
            }
            catch (Exception exception)
            {
                LogError("Prices: nie udało się zapisać cen początkowych: " + exception.Message, 8);
            }
        }

        private void ResetRunCounters()
        {
            _abortRun = false;
            _changed = 0;
            _atMinimum = 0;
            _skippedOtherCurrency = 0;
            _skippedLocked = 0;
            _unreadable = 0;
        }

        private void FinishRun()
        {
            SaveState();
            _lastSummary = "zmieniono: " + _changed + ", minimum: " + _atMinimum +
                           ", inne waluty: " + _skippedOtherCurrency + ", zablokowane: " + _skippedLocked +
                           (_unreadable > 0 ? ", bez odczytu: " + _unreadable : string.Empty) +
                           (_abortRun ? " (przerwano)" : string.Empty);
            LogMessage("Prices: " + _lastSummary, 8);
        }

        private bool CancellationRequested()
        {
            // Panel visibility is deliberately not sampled here. Committing a price makes the
            // Merchant Shop disappear for a frame or two; WaitForPanel distinguishes that blink
            // from a panel which was actually closed.
            return !GameController.Window.IsForeground() ||
                   (Control.MouseButtons & MouseButtons.Right) != 0;
        }

        private void StopWorker()
        {
            if (_worker != null && !_worker.IsDone)
                _worker.Done();
            Input.KeyUp(Keys.LControlKey);

            // Do not leave a half-edited listing behind during hot reload or shutdown.
            try
            {
                if (GameController != null && GameController.Window.IsForeground())
                    CancelPriceDialog();
            }
            catch
            {
                // Game state may already be disposed during shutdown.
            }
        }

        private string FormatRemainingTime()
        {
            var remaining = _nextRunUtc - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return ((int) remaining.TotalMinutes).ToString("00") + ":" + remaining.Seconds.ToString("00");
        }

        private sealed class PriceState
        {
            public int InitialPrice { get; set; }
            public int LastObservedPrice { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public DateTime LastReducedUtc { get; set; }
        }

        private sealed class MerchantItemTarget
        {
            public string Key { get; private set; }
            public string SlotKey { get; private set; }
            public uint InventoryId { get; private set; }
            public string Path { get; private set; }
            public int X { get; private set; }
            public int Y { get; private set; }

            public static MerchantItemTarget From(int tabIndex, NormalInventoryItem item)
            {
                try
                {
                    if (item == null || item.Item == null)
                        return null;

                    var inventoryId = item.Item.InventoryId;
                    var path = item.Item.Path ?? string.Empty;
                    var slotKey = "slot:" + tabIndex + ":" + item.InventPosX + ":" + item.InventPosY + ":" + path;
                    var key = inventoryId != 0
                        ? "item:" + inventoryId.ToString(CultureInfo.InvariantCulture) + ":" + path
                        : slotKey;

                    return new MerchantItemTarget
                    {
                        Key = key,
                        SlotKey = slotKey,
                        InventoryId = inventoryId,
                        Path = path,
                        X = item.InventPosX,
                        Y = item.InventPosY
                    };
                }
                catch
                {
                    return null;
                }
            }
        }

    }
}
