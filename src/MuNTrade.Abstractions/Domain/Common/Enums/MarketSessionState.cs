namespace MuNTrade.Abstractions.Domain.Common.Enums
{
    /// <summary>
    /// 定義市場盤態 (Session State)。
    /// </summary>
    public enum MarketSessionState
    {
        // Stock
        Stock_PreOpen,
        Stock_Open,
        Stock_Close,

        // Futures
        Fut_PreOpen,
        Fut_Day,
        Fut_Night,
        Fut_Close,

        // General
        Closed
    }
}

