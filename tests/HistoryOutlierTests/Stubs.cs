namespace Marketbuddy
{
    // 只給 RelistPricing／PriceAnomalyGuard 編得過：數值逐字照 Configuration 與 LastSoldPriceSource。
    internal sealed class Configuration
    {
        public const int MIN_PRICE = 1;
        public const int MAX_PRICE = 999999999;
        public bool AnomalyGuardEnabled = true;
        public int AnomalyGuardMinNormalPrice = 100000;
        public int AnomalyGuardRatio = 5;
    }

    internal static class LastSoldPriceSource
    {
        internal static uint RoundDownToHundred(long unitPrice)
        {
            var floored = unitPrice <= 0 ? 0L : unitPrice / 100L * 100L;
            if (floored < 100L)
                floored = 100L;
            if (floored > Configuration.MAX_PRICE)
                floored = Configuration.MAX_PRICE;
            return (uint)floored;
        }
    }
}
