using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace Merrchant
{
    public class MerrchantSettings : ISettings
    {
        public MerrchantSettings()
        {
            Enable = new ToggleNode(false);
            IntervalMinutes = new RangeNode<int>(20, 1, 240);
            AutoReduce = new ToggleNode(true);
            ReduceHotkey = Keys.F6;
            ResetHotkey = Keys.F7;
            ExtraDelay = new RangeNode<int>(80, 0, 1500);
            ShopTabName = string.Empty;
            MerchantDialog = new ToggleNode(true);
            ShowOverlay = new ToggleNode(true);
            ShowItemLabels = new ToggleNode(true);
            OverlayX = new RangeNode<int>(20, 0, 2000);
            OverlayY = new RangeNode<int>(180, 0, 2000);
        }

        public ToggleNode Enable { get; set; }

        [Menu("Interval (minutes)", "Every X minutes all chaos-priced shop items are reduced by 10%. Default: 20.")]
        public RangeNode<int> IntervalMinutes { get; set; }

        [Menu("Auto reduce", "When the timer expires and the merchant shop stash is open, reduce prices automatically.")]
        public ToggleNode AutoReduce { get; set; }

        [Menu("Reduce now hotkey", "Force a price reduction pass immediately.")]
        public HotkeyNode ReduceHotkey { get; set; }

        [Menu("Reset originals hotkey", "Forget stored original prices. Next seen price becomes the new original.")]
        public HotkeyNode ResetHotkey { get; set; }

        [Menu("Extra delay (ms)", "Added on top of game latency between UI actions.")]
        public RangeNode<int> ExtraDelay { get; set; }

        [Menu("Shop tab name", "Optional. Leave empty to use the currently visible stash tab.")]
        public string ShopTabName { get; set; }

        [Menu("Merchant dialog", "On: type only the number (merchant shop). Off: paste a ~price note (public stash).")]
        public ToggleNode MerchantDialog { get; set; }

        [Menu("Show overlay")]
        public ToggleNode ShowOverlay { get; set; }

        [Menu("Show item labels")]
        public ToggleNode ShowItemLabels { get; set; }

        [Menu("Overlay X")]
        public RangeNode<int> OverlayX { get; set; }

        [Menu("Overlay Y")]
        public RangeNode<int> OverlayY { get; set; }
    }
}
