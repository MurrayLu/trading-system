using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Data
{
    public interface IMarketDataPipeline : IDisposable
    {
        /// <summary>
        /// 啟用或更新一個 Symbol 的資料處理管線。
        /// </summary>
        /// <param name="symbol">商品代號</param>
        /// <param name="ticksEnabled">是否啟用逐筆成交與五檔（會觸發對外訂閱）</param>
        /// <param name="klineEnabled">是否啟用 K 線生成</param>
        /// <param name="indicators">要啟用的指標名稱列表</param>
        void EnableSymbol(
            string symbol,
            bool ticksEnabled = false,
            bool klineEnabled = true,
            IEnumerable<string>? indicators = null);

        /// <summary>
        /// 停用一個 Symbol 的所有資料處理，並釋放相關資源。
        /// </summary>
        /// <param name="symbol">商品代號</param>
        void DisableSymbol(string symbol);
    }
}
