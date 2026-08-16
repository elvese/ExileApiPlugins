# Merrchant

Periodically reduces prices of items listed in your merchant shop stash.

## Rules

- Only items priced in **Chaos Orbs** are touched (`~price` / `~b/o` notes, or merchant-tab chaos prices).
- Every **X minutes** (default **20**) every matching item is reduced by **10%**.
- Price never goes below **30%** of the original (first seen) price. Minimum listing is 1 chaos.
- Reduction uses `floor(current * 0.9)` so a 2c item becomes 1c, unless that would break the 30% floor.

## Usage

1. Compile the plugin into `PoeHelper\Plugins\Compiled\Merrchant`.
2. Enable **Merrchant** in the HUD plugin list.
3. Open your merchant shop stash tab (optionally set **Shop tab name** in settings).
4. Leave the shop open when the timer expires, or press **F6** to reduce immediately.
5. Press **Escape** to abort a running pass. **F7** forgets stored original prices.

The overlay shows the countdown, how many chaos listings were found, and how many are already at the 30% floor. Item labels show `9c>8c` or `3c MIN`.

Original prices are remembered in `PriceStore.json` next to the compiled plugin so a restart does not reset the floor.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| Interval (minutes) | 20 | How often prices drop by 10% |
| Auto reduce | on | Run automatically when the timer is due and the shop is open |
| Reduce now hotkey | F6 | Force one pass now |
| Reset originals hotkey | F7 | Treat the next seen prices as new originals |
| Extra delay (ms) | 80 | Extra wait between clicks / keypresses |
| Shop tab name | empty | If set, only that stash tab is processed |
| Merchant dialog | on | Type the number only. Turn off to paste `~price X chaos` notes |

The game window must be focused. If the timer expires while the shop is closed, Merrchant waits and runs the next time you open it.
