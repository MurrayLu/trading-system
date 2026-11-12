namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義策略的交易方向限制。
    /// </summary>
    public enum TradingMode
    {
        LongOnly = 0,   // 只做多
        ShortOnly = 1,  // 只做空
        LongShort = 2   // 多空都做
    }
}

