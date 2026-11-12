namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義回檔的品質等級。
    /// </summary>
    public enum PullbackQuality
    {
        A,  // 優質
        B,  // 普通
        C,  // 劣質
        Fail // 失敗/結構破壞
    }
}

