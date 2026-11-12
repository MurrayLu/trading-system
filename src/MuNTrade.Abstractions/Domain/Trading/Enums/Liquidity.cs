namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義訂單成交時的角色（提供流動性或消耗流動性）。
    /// </summary>
    public enum Liquidity
    {
        Maker,  // 掛單者 (提供流動性)
        Taker,  // 吃單者 (消耗流動性)
        Unknown
    }
}

