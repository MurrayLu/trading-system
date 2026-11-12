using System; // Keep for Obsolete attribute
using MuNTrade.Abstractions.Domain.MarketData;

namespace MuNTrade.Abstractions.Contracts.Market
{
    /// <summary>
    /// 市場資訊查詢用途（用於決定錯誤處理策略）
    /// </summary>
    public enum MarketInfoPurpose
    {
        /// <summary>UI 顯示用途（允許 Unknown，不拋錯）</summary>
        UI,

        /// <summary>風險評估用途（Unknown 必須拋錯阻擋）</summary>
        Risk,

        /// <summary>訂單執行用途（Unknown 必須拋錯阻擋）</summary>
        Execution
    }

    /// <summary>
    /// 提供商品市場資訊的查詢介面 (Single Source of Truth)。
    /// </summary>
    public interface IMarketInfoProvider
    {
        /// <summary>
        /// 嘗試取得市場資訊（UI 專用，不拋錯、不 log）。
        /// </summary>
        /// <param name="symbol">商品代號</param>
        /// <param name="info">輸出參數：市場資訊</param>
        /// <returns>若快取中有有效資訊則回傳 true，否則回傳 false</returns>
        bool TryGetMarketInfo(string symbol, out MarketInfo info);

        /// <summary>
        /// 取得市場資訊，若 Unknown 則根據用途決定是否拋錯（Risk/Execution 必須拋錯）。
        /// </summary>
        /// <param name="symbol">商品代號</param>
        /// <param name="purpose">查詢用途</param>
        /// <returns>市場資訊</returns>
        /// <exception cref="InvalidOperationException">當 purpose 為 Risk 或 Execution 且資訊為 Unknown 時拋出</exception>
        MarketInfo GetMarketInfoOrThrow(string symbol, MarketInfoPurpose purpose);

        /// <summary>
        /// [Obsolete] 請改用 TryGetMarketInfo 或 GetMarketInfoOrThrow
        /// </summary>
        /// <summary>
        /// Warm the cache from trusted sources (position snapshot / instrument master).
        /// Must NOT be called from UI rendering paths.
        /// </summary>
        void UpsertFromSnapshot(string symbol, MuNTrade.Abstractions.Domain.Common.Enums.MarketNo marketNo, MuNTrade.Abstractions.Domain.Common.Enums.MarketSource source);

        [Obsolete("Use TryGetMarketInfo (UI) or GetMarketInfoOrThrow (Risk/Execution) instead.")]
        MarketInfo GetMarketInfo(string symbol);
    }
}

