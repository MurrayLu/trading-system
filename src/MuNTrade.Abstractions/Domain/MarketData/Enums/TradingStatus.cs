namespace MuNTrade.Abstractions.Domain.MarketData.Enums
{
    /// <summary>
    /// 定義股票的交易狀態（注意/處置）。
    /// </summary>
    public enum TradingStatus
    {
        Normal,      // 一般（預設風險狀態）
        Watchlist,   // 注意股
        Disposal     // 處置股
    }
}

