namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義策略訊號的具體動作，具有清晰的策略意圖。
    /// </summary>
    public enum SignalAction
    {
        /// <summary>
        /// 建立多頭部位的訊號。
        /// </summary>
        LongEntry,

        /// <summary>
        /// 平倉多頭部位的訊號。
        /// </summary>
        LongExit,

        /// <summary>
        /// 建立空頭部位的訊號。
        /// </summary>
        ShortEntry,

        /// <summary>
        /// 平倉空頭部位的訊號。
        /// </summary>
        ShortExit,

        /// <summary>
        /// 表示無操作或繼續持有的訊號。
        /// </summary>
        Hold
    }
}

