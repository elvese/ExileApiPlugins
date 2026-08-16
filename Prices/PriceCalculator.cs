using System;

namespace Prices
{
    /// <summary>
    /// Pure pricing rules used by the plugin. Prices in Path of Exile are integers, therefore a
    /// 10% reduction is rounded down. The 30% limit is rounded up so a listing can never fall below
    /// 30% of its recorded starting price.
    /// </summary>
    public static class PriceCalculator
    {
        public const int ReductionPercent = 10;
        public const int MinimumPercent = 30;

        public static int GetMinimumPrice(int initialPrice)
        {
            if (initialPrice <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialPrice));

            return (int) Math.Max(1L, ((long) initialPrice * MinimumPercent + 99L) / 100L);
        }

        public static int GetNextPrice(int initialPrice, int currentPrice)
        {
            if (initialPrice <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialPrice));
            if (currentPrice <= 0)
                throw new ArgumentOutOfRangeException(nameof(currentPrice));

            var minimumPrice = GetMinimumPrice(initialPrice);

            // Never raise a price which was manually moved below the plugin's saved floor.
            if (currentPrice <= minimumPrice)
                return currentPrice;

            var reducedPrice = (int) ((long) currentPrice * (100 - ReductionPercent) / 100L);
            return Math.Max(minimumPrice, reducedPrice);
        }
    }
}
