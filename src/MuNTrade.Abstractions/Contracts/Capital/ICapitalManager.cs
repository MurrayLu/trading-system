using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Capital
{
    public interface ICapitalManager
    {
        /// <summary>
        /// 根據風險、資金與級距表，計算建議下單口數/張數
        /// </summary>
        /// <param name="symbol">商品代碼 (用於判斷期貨/股票)</param>
        /// <param name="entryPrice">預計進場價</param>
        /// <param name="stopPrice">預計停損價</param>
        /// <returns>建議下單數量 (股數或口數)</returns>
        Task<int> CalculatePositionSizeAsync(string symbol, decimal entryPrice, decimal stopPrice);

        /// <summary>
        /// 取得該商品的級距上限 (僅依資金查詢，不看風險)
        /// </summary>
        Task<int> GetMaxPositionSizeAsync(string symbol, decimal price);
    }
}

