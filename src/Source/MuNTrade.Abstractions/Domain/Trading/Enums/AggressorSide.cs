namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義逐筆成交中的攻擊方。
    /// </summary>
    public enum AggressorSide
    {
        Unknown, // 無法判斷
        Buy,     // 買方攻擊 (外盤)
        Sell     // 賣方攻擊 (內盤)
    }
}

