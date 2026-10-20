namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義交易類別 (現股/融資/融券/無券)。
    /// </summary>
    public enum TradeType
    {
        Common,     // 現股
        Margin,     // 融資
        Short,      // 融券
        NoTicket    // 無券
    }
}

