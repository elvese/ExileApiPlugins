using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace Prices
{
    public class PricesSettings : ISettings
    {
        public PricesSettings()
        {
            Enable = new ToggleNode(true);
            AutomaticRepricing = new ToggleNode(true);
            RunNowHotkey = new HotkeyNode(Keys.F8);
            ActionDelay = new RangeNode<int>(75, 25, 500);
            ShowStatus = new ToggleNode(true);
        }

        public ToggleNode Enable { get; set; }

        [Menu("Automatycznie co 5 minut", "Uruchamia obniżanie cen co 5 minut. Gdy Merchant Shop jest zamknięty, operacja czeka na jego otwarcie.")]
        public ToggleNode AutomaticRepricing { get; set; }

        [Menu("Uruchom teraz", "Ręcznie uruchamia pełny przebieg. Domyślnie F8.")]
        public HotkeyNode RunNowHotkey { get; set; }

        [Menu("Opóźnienie akcji (ms)", "Przerwa pomiędzy akcjami myszy i klawiatury.")]
        public RangeNode<int> ActionDelay { get; set; }

        [Menu("Pokaż status", "Pokazuje licznik i wynik ostatniego przebiegu, gdy Merchant Shop jest otwarty.")]
        public ToggleNode ShowStatus { get; set; }
    }
}
