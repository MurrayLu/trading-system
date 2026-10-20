namespace MuNTrade.Abstractions.Domain.MarketData.Enums
{
    /// <summary>
    /// 定義股票的當沖資格。
    /// </summary>
    public enum DayTradeStatus
    {
        NotAllowed,         // 0: 不可當沖
        BuyThenSell,        // 1: 可先買後賣
        Bidirectional       // 2: 可雙向當沖
    }
}

