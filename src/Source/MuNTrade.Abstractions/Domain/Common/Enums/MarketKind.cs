namespace MuNTrade.Abstractions.Domain.Common.Enums
{
    /// <summary>
    /// 定義市場類型 (Securities/Derivatives)。
    /// </summary>
    public enum MarketKind
    {
        Unknown,

        /// <summary>
        /// 證券 (Stock, ETF, Warrant, etc.)
        /// </summary>
        Securities,

        /// <summary>
        /// 衍生性金融商品 (Futures, Options)
        /// </summary>
        Derivatives
    }
}

