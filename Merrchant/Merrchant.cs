using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using SharpDX;

namespace Merrchant
{
    public class MerrchantCore : BaseSettingsPlugin<MerrchantSettings>
    {
        private const string CoroutineName = "Merrchant Reduce Prices";
        private const int WorkTimeoutMs = 90000;

        private readonly Stopwatch _workTimer = new Stopwatch();
        private readonly WaitTime _wait10 = new WaitTime(10);
        private readonly WaitTime _wait25 = new WaitTime(25);

        private PriceStore _store;
        private Coroutine _worker;
        private string _status = "Idle";
        private int _lastReduced;
        private int _lastSkipped;
        private int _lastFailed;
        private DateTime _nextReductionUtc;
        private Vector2 _windowOffset;

        public MerrchantCore()
        {
            Name = "Merrchant";
        }

        public override bool Initialise()
        {
            _store = new PriceStore(Path.Combine(DirectoryFullName, "PriceStore.json"));
            _store.Load();

            if (_store.LastReductionUtc.HasValue)
                _nextReductionUtc = _store.LastReductionUtc.Value.AddMinutes(Settings.IntervalMinutes.Value);
            else
                RestartTimer();

            Input.RegisterKey(Settings.ReduceHotkey);
            Input.RegisterKey(Settings.ResetHotkey);
            Input.RegisterKey(Keys.Escape);
            Input.RegisterKey(Keys.LControlKey);

            Settings.ReduceHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ReduceHotkey);
            Settings.ResetHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ResetHotkey);
            Settings.IntervalMinutes.OnValueChanged += (sender, value) => RecalculateNextReduction();

            return true;
        }

        public override void OnPluginDestroyForHotReload()
        {
            StopWorker();
        }

        public override void OnClose()
        {
            StopWorker();
            try
            {
                _store?.Save();
            }
            catch
            {
                // ignored
            }
        }

        public override Job Tick()
        {
            if (!Settings.Enable)
            {
                if (IsWorkerRunning())
                    StopWorker();
                return null;
            }

            if (Settings.ResetHotkey.PressedOnce())
            {
                _store.Clear();
                _store.Save();
                _status = "Original prices reset";
                LogMessage("Merrchant: original prices cleared. Next seen chaos price becomes the original.", 8);
            }

            if (Settings.ReduceHotkey.PressedOnce())
                TryStartReduction("hotkey");

            if (Settings.AutoReduce && DateTime.UtcNow >= _nextReductionUtc)
                TryStartReduction("timer");

            if (IsWorkerRunning() && _workTimer.ElapsedMilliseconds > WorkTimeoutMs)
            {
                LogError("Merrchant: stopped because a reduce pass ran longer than 90 seconds.", 8);
                StopWorker();
                _status = "Stopped (timeout)";
            }

            if (IsWorkerRunning() && Input.GetKeyState(Keys.Escape))
            {
                StopWorker();
                _status = "Cancelled";
            }

            return null;
        }

        public override void Render()
        {
            if (!Settings.Enable)
                return;

            var shop = GetVisibleShop();
            if (shop != null)
                RefreshKnownItems(shop);

            if (Settings.ShowOverlay)
                DrawOverlay(shop);

            if (Settings.ShowItemLabels && shop != null)
                DrawItemLabels(shop);
        }

        private void TryStartReduction(string reason)
        {
            if (IsWorkerRunning())
                return;

            if (!GameController.Window.IsForeground())
            {
                _status = "Waiting: game is not focused";
                return;
            }

            var shop = GetVisibleShop();
            if (shop == null)
            {
                _status = "Waiting: open merchant shop stash";
                return;
            }

            _worker = new Coroutine(ReducePrices(shop, reason), this, CoroutineName);
            Core.ParallelRunner.Run(_worker);
        }

        private IEnumerator ReducePrices(ShopSnapshot shop, string reason)
        {
            _workTimer.Restart();
            _lastReduced = 0;
            _lastSkipped = 0;
            _lastFailed = 0;
            _status = "Reducing chaos prices...";
            LogMessage($"Merrchant: starting reduce pass ({reason}).", 6);

            var cursorBefore = Input.ForceMousePosition;
            _windowOffset = GameController.Window.GetWindowRectangle().TopLeft;
            var latency = GetLatency();

            var aborted = false;
            var items = shop.Items.Where(x => x.Item != null && x.Item.Address != 0 && x.Item.IsValid).ToList();
            foreach (var invItem in items)
            {
                if (!Settings.Enable || !IsShopStillOpen())
                {
                    aborted = true;
                    _status = "Aborted: shop closed";
                    break;
                }

                var parsed = default(ParsedPrice);
                yield return ReadItemPrice(invItem, latency, p => parsed = p);

                if (parsed == null || !parsed.IsChaos)
                {
                    _lastSkipped++;
                    continue;
                }

                var identity = BuildIdentity(shop, invItem);
                var record = _store.Remember(identity.Key, identity.Fingerprint, identity.Name, parsed.Amount, parsed.Style);
                var nextPrice = PriceParser.ComputeReducedPrice(parsed.Amount, record.OriginalPrice);

                if (nextPrice >= parsed.Amount)
                {
                    _lastSkipped++;
                    continue;
                }

                var applied = false;
                yield return ApplyNewPrice(invItem, parsed, nextPrice, latency, ok => applied = ok);

                if (applied)
                {
                    record.LastSeenPrice = nextPrice;
                    _lastReduced++;
                    LogMessage($"Merrchant: {identity.Name} {parsed.Amount}c -> {nextPrice}c (orig {record.OriginalPrice}c).", 4);
                }
                else
                    _lastFailed++;

                yield return new WaitTime(latency + Settings.ExtraDelay.Value);
            }

            if (!aborted)
            {
                _store.LastReductionUtc = DateTime.UtcNow;
                RestartTimer();
            }

            try
            {
                _store.Save();
            }
            catch (Exception ex)
            {
                LogError($"Merrchant: failed to save price store: {ex.Message}", 6);
            }

            yield return Input.SetCursorPositionSmooth(cursorBefore);
            Input.MouseMove();

            if (!aborted)
                _status = $"Done: -{_lastReduced}  skip {_lastSkipped}  fail {_lastFailed}";
            else
                _status = $"Aborted: -{_lastReduced}  skip {_lastSkipped}  fail {_lastFailed}";

            LogMessage($"Merrchant: {_status}", 8, Color.Orange);
            _workTimer.Reset();
            _worker = null;
        }

        private IEnumerator ReadItemPrice(NormalInventoryItem invItem, int latency, Action<ParsedPrice> done)
        {
            var fromMemory = ReadNoteFromMemory(invItem.Item);
            var parsed = PriceParser.TryParse(fromMemory);
            if (parsed != null && parsed.IsChaos)
            {
                done(parsed);
                yield break;
            }

            var center = invItem.GetClientRect().Center + _windowOffset;
            yield return Input.SetCursorPositionSmooth(center);
            yield return new WaitTime(Math.Max(40, latency / 2) + Settings.ExtraDelay.Value);
            Input.MouseMove();

            var tooltipText = CollectElementText(GetHoverTooltip());
            parsed = PriceParser.TryParse(fromMemory + Environment.NewLine + tooltipText);
            if (parsed != null)
            {
                done(parsed);
                yield break;
            }

            var previous = SafeGetClipboard();
            yield return SendHotkey(Keys.C, true);
            yield return new WaitTime(latency);
            var copied = SafeGetClipboard();
            SafeSetClipboard(previous);

            parsed = PriceParser.TryParse(copied);
            done(parsed);
        }

        private IEnumerator ApplyNewPrice(NormalInventoryItem invItem, ParsedPrice current, int newPrice, int latency,
            Action<bool> done)
        {
            var center = invItem.GetClientRect().Center + _windowOffset;
            yield return Input.SetCursorPositionSmooth(center);
            yield return new WaitTime(Math.Max(30, Settings.ExtraDelay.Value));
            Input.Click(MouseButtons.Right);
            yield return new WaitTime(latency + Settings.ExtraDelay.Value + 40);

            if (Settings.MerchantDialog)
            {
                yield return SendHotkey(Keys.A, true);
                yield return new WaitTime(30);
                yield return TypeNumber(newPrice);
            }
            else
            {
                var note = PriceParser.BuildNote(current.Style, newPrice);
                SafeSetClipboard(note);
                yield return SendHotkey(Keys.A, true);
                yield return new WaitTime(25);
                yield return SendHotkey(Keys.V, true);
            }

            yield return new WaitTime(40 + Settings.ExtraDelay.Value);
            yield return Input.KeyPress(Keys.Enter);
            yield return new WaitTime(latency + Settings.ExtraDelay.Value);
            done(true);
        }

        private IEnumerator TypeNumber(int value)
        {
            var digits = Math.Max(0, value).ToString();
            foreach (var digit in digits)
            {
                yield return Input.KeyPress((Keys) (Keys.D0 + (digit - '0')));
                yield return _wait25;
            }
        }

        private IEnumerator SendHotkey(Keys key, bool withControl)
        {
            if (withControl)
            {
                Input.KeyDown(Keys.LControlKey);
                yield return _wait10;
            }

            yield return Input.KeyPress(key);
            yield return _wait10;

            if (withControl)
                Input.KeyUp(Keys.LControlKey);
        }

        private void RefreshKnownItems(ShopSnapshot shop)
        {
            var before = _store.Count;
            foreach (var invItem in shop.Items)
            {
                if (invItem?.Item == null || invItem.Item.Address == 0)
                    continue;

                var note = ReadNoteFromMemory(invItem.Item);
                var parsed = PriceParser.TryParse(note);
                if (parsed == null || !parsed.IsChaos)
                    continue;

                var identity = BuildIdentity(shop, invItem);
                _store.Remember(identity.Key, identity.Fingerprint, identity.Name, parsed.Amount, parsed.Style);
            }

            if (_store.Count != before)
            {
                try
                {
                    _store.Save();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void DrawOverlay(ShopSnapshot shop)
        {
            var remaining = _nextReductionUtc - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            var chaosCount = 0;
            var floorCount = 0;
            if (shop != null)
            {
                foreach (var invItem in shop.Items)
                {
                    if (invItem?.Item == null || invItem.Item.Address == 0)
                        continue;

                    var identity = BuildIdentity(shop, invItem);
                    var record = _store.Find(identity.Key, identity.Fingerprint);
                    var parsed = PriceParser.TryParse(ReadNoteFromMemory(invItem.Item));
                    var current = parsed != null && parsed.IsChaos
                        ? parsed.Amount
                        : record?.LastSeenPrice ?? 0;
                    if (current <= 0)
                        continue;

                    chaosCount++;
                    var original = record?.OriginalPrice ?? current;
                    if (!PriceParser.CanReduce(current, original))
                        floorCount++;
                }
            }

            var lines = new[]
            {
                "Merrchant  -10% / min 30% orig",
                shop == null ? "Shop: closed" : $"Shop: {shop.TabName}",
                $"Next: {remaining.Minutes:00}:{remaining.Seconds:00}  (every {Settings.IntervalMinutes.Value} min)",
                $"Chaos listings: {chaosCount}   at floor: {floorCount}",
                $"Status: {_status}"
            };

            const int pad = 8;
            const int lineH = 18;
            var width = 0f;
            foreach (var line in lines)
                width = Math.Max(width, Graphics.MeasureText(line, 15).X);

            var rect = new RectangleF(Settings.OverlayX.Value, Settings.OverlayY.Value, width + pad * 2,
                lines.Length * lineH + pad * 2);
            Graphics.DrawBox(rect, new Color(0, 0, 0, 180));
            Graphics.DrawFrame(rect, new Color(220, 160, 40, 220), 1);

            var pos = new Vector2(rect.X + pad, rect.Y + pad);
            for (var i = 0; i < lines.Length; i++)
            {
                var color = i == 0 ? new Color(255, 196, 72) : Color.White;
                if (i == 4 && _status.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase))
                    color = Color.Orange;
                Graphics.DrawText(lines[i], pos, color, 15);
                pos.Y += lineH;
            }
        }

        private void DrawItemLabels(ShopSnapshot shop)
        {
            foreach (var invItem in shop.Items)
            {
                if (invItem?.Item == null || invItem.Item.Address == 0)
                    continue;

                var identity = BuildIdentity(shop, invItem);
                var record = _store.Find(identity.Key, identity.Fingerprint);
                var parsed = PriceParser.TryParse(ReadNoteFromMemory(invItem.Item));
                if ((parsed == null || !parsed.IsChaos) && record == null)
                    continue;

                var current = parsed != null && parsed.IsChaos ? parsed.Amount : record.LastSeenPrice;
                var original = record?.OriginalPrice ?? current;
                var next = PriceParser.ComputeReducedPrice(current, original);
                var atFloor = next >= current;
                var text = atFloor
                    ? $"{current}c MIN"
                    : $"{current}c>{next}c";

                var box = invItem.GetClientRect();
                var size = Graphics.MeasureText(text, 13);
                var label = new RectangleF(box.X + 2, box.Y + 2, size.X + 6, size.Y + 2);
                Graphics.DrawBox(label, new Color(0, 0, 0, 170));
                Graphics.DrawText(text, new Vector2(label.X + 3, label.Y),
                    atFloor ? Color.Gray : new Color(255, 210, 80), 13);
            }
        }

        private ShopSnapshot GetVisibleShop()
        {
            try
            {
                var stash = GameController.IngameState.IngameUi.StashElement;
                if (stash == null || !stash.IsVisible)
                    return null;

                var visible = stash.VisibleStash;
                var items = visible?.VisibleInventoryItems;
                if (items == null)
                    return null;

                var tabName = ReadTabName(stash);
                var wanted = Settings.ShopTabName;
                if (!string.IsNullOrWhiteSpace(wanted) &&
                    !string.Equals(tabName, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                    return null;

                return new ShopSnapshot
                {
                    TabName = string.IsNullOrEmpty(tabName) ? "visible" : tabName,
                    TabIndex = stash.IndexVisibleStash,
                    Items = items.ToList()
                };
            }
            catch
            {
                return null;
            }
        }

        private bool IsShopStillOpen()
        {
            try
            {
                var stash = GameController.IngameState.IngameUi.StashElement;
                return stash != null && stash.IsVisible && stash.VisibleStash != null;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadTabName(StashElement stash)
        {
            try
            {
                var index = stash.IndexVisibleStash;
                var name = stash.GetStashName(index);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;

                var names = stash.AllStashNames;
                if (names != null && index >= 0 && index < names.Count)
                    return names[index];
            }
            catch
            {
                // ignored
            }

            return string.Empty;
        }

        private Element GetHoverTooltip()
        {
            try
            {
                var hover = GameController.Game.IngameState.UIHover;
                if (hover == null || hover.Address == 0)
                    return null;

                return hover.AsObject<HoverItemIcon>()?.Tooltip;
            }
            catch
            {
                return null;
            }
        }

        private ItemIdentity BuildIdentity(ShopSnapshot shop, NormalInventoryItem invItem)
        {
            var item = invItem.Item;
            var baseName = string.Empty;
            var uniqueName = string.Empty;
            var itemLevel = 0;
            long modsHash = 0;

            try
            {
                var bit = GameController.Files.BaseItemTypes.Translate(item.Path);
                baseName = bit?.BaseName ?? item.Path ?? string.Empty;
            }
            catch
            {
                baseName = item.Path ?? string.Empty;
            }

            try
            {
                var mods = item.GetComponent<Mods>();
                if (mods != null)
                {
                    uniqueName = mods.UniqueName ?? string.Empty;
                    itemLevel = mods.ItemLevel;
                    modsHash = mods.Hash;
                }
            }
            catch
            {
                // ignored
            }

            var fingerprint = $"{baseName}|{uniqueName}|{itemLevel}|{modsHash}";
            var key = $"{shop.TabIndex}:{invItem.InventPosX}:{invItem.InventPosY}|{fingerprint}";
            var display = string.IsNullOrWhiteSpace(uniqueName) ? baseName : uniqueName;
            if (string.IsNullOrWhiteSpace(display))
                display = "item";

            return new ItemIdentity
            {
                Key = key,
                Fingerprint = fingerprint,
                Name = display
            };
        }

        private static string ReadNoteFromMemory(Entity item)
        {
            if (item == null || item.Address == 0)
                return string.Empty;

            var buffer = new StringBuilder();
            TryReadStringMembers(item, buffer);

            try
            {
                if (item.HasComponent<Mods>())
                    TryReadStringMembers(item.GetComponent<Mods>(), buffer);
            }
            catch
            {
                // ignored
            }

            try
            {
                if (item.HasComponent<Base>())
                    TryReadStringMembers(item.GetComponent<Base>(), buffer);
            }
            catch
            {
                // ignored
            }

            return buffer.ToString();
        }

        private static void TryReadStringMembers(object target, StringBuilder buffer)
        {
            if (target == null)
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var name in new[] {"Note", "ItemNote", "HumanNote", "PublicPrice", "Price"})
            {
                try
                {
                    var prop = target.GetType().GetProperty(name, flags);
                    var value = prop?.GetValue(target, null) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        buffer.AppendLine(value);
                }
                catch
                {
                    // ignored
                }

                try
                {
                    var field = target.GetType().GetField(name, flags);
                    var value = field?.GetValue(target) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        buffer.AppendLine(value);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static string CollectElementText(Element element)
        {
            if (element == null || element.Address == 0)
                return string.Empty;

            var buffer = new StringBuilder();
            CollectElementText(element, buffer, 0);
            return buffer.ToString();
        }

        private static void CollectElementText(Element element, StringBuilder buffer, int depth)
        {
            if (element == null || element.Address == 0 || depth > 12)
                return;

            try
            {
                if (!string.IsNullOrWhiteSpace(element.Text))
                    buffer.AppendLine(element.Text);
            }
            catch
            {
                // ignored
            }

            IList<Element> children;
            try
            {
                children = element.Children;
            }
            catch
            {
                return;
            }

            if (children == null)
                return;

            foreach (var child in children)
                CollectElementText(child, buffer, depth + 1);
        }

        private int GetLatency()
        {
            try
            {
                return Math.Max(30, (int) GameController.IngameState.CurLatency);
            }
            catch
            {
                return 80;
            }
        }

        private void RestartTimer()
        {
            _nextReductionUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, Settings.IntervalMinutes.Value));
        }

        private void RecalculateNextReduction()
        {
            if (_store?.LastReductionUtc != null)
                _nextReductionUtc = _store.LastReductionUtc.Value.AddMinutes(Math.Max(1, Settings.IntervalMinutes.Value));
            else
                RestartTimer();
        }

        private bool IsWorkerRunning()
        {
            return _worker != null && !_worker.IsDone;
        }

        private void StopWorker()
        {
            try
            {
                Input.KeyUp(Keys.LControlKey);
            }
            catch
            {
                // ignored
            }

            var running = Core.ParallelRunner?.FindByName(CoroutineName);
            running?.Done();
            _worker = null;
            _workTimer.Reset();
        }

        private static string SafeGetClipboard()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void SafeSetClipboard(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                    Clipboard.Clear();
                else
                    Clipboard.SetText(text);
            }
            catch
            {
                // ignored
            }
        }

        private class ShopSnapshot
        {
            public string TabName;
            public int TabIndex;
            public List<NormalInventoryItem> Items;
        }

        private class ItemIdentity
        {
            public string Key;
            public string Fingerprint;
            public string Name;
        }
    }
}
