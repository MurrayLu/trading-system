namespace MuNTrade.Abstractions.Contracts.Trading
{
    /// <summary>
    /// 提供唯讀的部位狀態查詢功能。
    /// </summary>
    public interface IPositionReader
    {
        /// <summary>
        /// 檢查指定商品當前是否有任何部位。
        /// </summary>
        bool HasPosition(string symbol);
    }
}
