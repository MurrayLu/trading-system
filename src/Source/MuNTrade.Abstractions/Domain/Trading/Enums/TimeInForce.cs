namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義訂單的有效時間。
    /// </summary>
    public enum TimeInForce
    {
        // 當日有效 (Rest of Day)
        ROD,

        // 立即成交或取消 (Immediate Or Cancel)，允許部分成交。
        IOC,

        // 全部成交或取消 (Fill Or Kill)，不允許部分成交。
        FOK
    }
}

