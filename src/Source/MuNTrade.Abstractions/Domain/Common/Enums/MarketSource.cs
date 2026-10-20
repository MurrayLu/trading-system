namespace MuNTrade.Abstractions.Domain.Common.Enums
{
    /// <summary>
    /// 市場資料來源 (國內/海外)
    /// </summary>
    public enum MarketSource
    {
        Unknown = 0,

        /// <summary>
        /// 國內 (SKQuoteLib)
        /// </summary>
        Domestic,

        /// <summary>
        /// 海外 (SKOSQuoteLib) or Foreign Source
        /// </summary>
        Overseas
    }
}

