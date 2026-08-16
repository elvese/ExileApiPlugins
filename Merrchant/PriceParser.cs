using System;
using System.Text.RegularExpressions;

namespace Merrchant
{
    public class ParsedPrice
    {
        public int Amount;
        public string Style;
        public string Currency;
        public bool IsChaos;
        public string Raw;
    }

    public static class PriceParser
    {
        public const double ReductionFactor = 0.10;
        public const double MinFractionOfOriginal = 0.30;

        private static readonly Regex TradeNoteRegex = new Regex(
            @"~(?<style>price|b/o|c/o)\s+(?<amt>\d+)\s*(?<cur>chaos(?:\s*orbs?)?|c|divine(?:\s*orbs?)?|div|exalted(?:\s*orbs?)?|exalt|ex|mirror(?:\s*of\s*kalandra)?|mir|alch(?:emy)?|alt|fuse|chrom|jew|chance|regal|gcp|chisel|scour|blessed|vaal|annul|whetstone|scrap)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MerchantPriceRegex = new Regex(
            @"(?:(?:note|price)\s*:?\s*(?<amt>\d+)\s*(?<cur>chaos(?:\s*orbs?)?)\b)|(?:(?<amt2>\d+)\s+chaos\s+orbs?\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BareNumberRegex = new Regex(
            @"^\s*(?<amt>\d+)\s*$",
            RegexOptions.Compiled);

        public static ParsedPrice TryParse(string text, bool allowBareNumber = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var note = TradeNoteRegex.Match(text);
            if (note.Success)
            {
                var amount = int.Parse(note.Groups["amt"].Value);
                var currency = note.Groups["cur"].Value.Trim();
                var chaos = IsChaosCurrency(currency);
                return new ParsedPrice
                {
                    Amount = amount,
                    Style = note.Groups["style"].Value.ToLowerInvariant(),
                    Currency = currency,
                    IsChaos = chaos,
                    Raw = note.Value
                };
            }

            var merchant = MerchantPriceRegex.Match(text);
            if (merchant.Success)
            {
                var amountText = merchant.Groups["amt"].Success && !string.IsNullOrEmpty(merchant.Groups["amt"].Value)
                    ? merchant.Groups["amt"].Value
                    : merchant.Groups["amt2"].Value;

                return new ParsedPrice
                {
                    Amount = int.Parse(amountText),
                    Style = "merchant",
                    Currency = "chaos",
                    IsChaos = true,
                    Raw = merchant.Value
                };
            }

            if (allowBareNumber)
            {
                var bare = BareNumberRegex.Match(text);
                if (bare.Success)
                {
                    return new ParsedPrice
                    {
                        Amount = int.Parse(bare.Groups["amt"].Value),
                        Style = "merchant",
                        Currency = "chaos",
                        IsChaos = true,
                        Raw = bare.Value.Trim()
                    };
                }
            }

            return null;
        }

        public static bool IsChaosCurrency(string currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
                return false;

            var value = Regex.Replace(currency.Trim().ToLowerInvariant(), @"\s+", "");
            return value == "c" || value == "chaos" || value == "chaosorb" || value == "chaosorbs";
        }

        public static int MinPrice(int originalPrice)
        {
            if (originalPrice <= 0)
                return 1;

            return Math.Max(1, (int) Math.Round(originalPrice * MinFractionOfOriginal));
        }

        public static int ComputeReducedPrice(int currentPrice, int originalPrice)
        {
            var floor = MinPrice(originalPrice);
            var reduced = (int) Math.Floor(currentPrice * (1.0 - ReductionFactor));
            return Math.Max(floor, reduced);
        }

        public static bool CanReduce(int currentPrice, int originalPrice)
        {
            return ComputeReducedPrice(currentPrice, originalPrice) < currentPrice;
        }

        public static string BuildNote(string style, int amount)
        {
            if (string.Equals(style, "b/o", StringComparison.OrdinalIgnoreCase))
                return "~b/o " + amount + " chaos";

            if (string.Equals(style, "c/o", StringComparison.OrdinalIgnoreCase))
                return "~c/o " + amount + " chaos";

            return "~price " + amount + " chaos";
        }
    }
}
