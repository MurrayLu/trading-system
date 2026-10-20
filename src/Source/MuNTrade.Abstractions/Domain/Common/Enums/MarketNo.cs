using System;

namespace MuNTrade.Abstractions.Domain.Common.Enums
{
    /// <summary>
    /// 市場代號 (對應券商 API 定義與 SKQuoteLib)
    /// </summary>
    public enum MarketNo : short
    {
        /// <summary>
        /// 未知/未指定
        /// </summary>
        Unknown = -1,

        /// <summary>
        /// 上市 (證券)
        /// </summary>
        Listing = 0,

        /// <summary>
        /// 上櫃 (證券)
        /// </summary>
        OTC = 1,

        /// <summary>
        /// 期貨 (國內期貨)
        /// </summary>
        Futures = 2,

        /// <summary>
        /// 選擇權 (國內選擇權)
        /// </summary>
        Options = 3,

        /// <summary>
        /// 興櫃 (證券)
        /// </summary>
        Emerging = 4,

        /// <summary>
        /// 盤中零股-上市
        /// </summary>
        IntradayOddListing = 5,

        /// <summary>
        /// 盤中零股-上櫃
        /// </summary>
        IntradayOddOTC = 6,

        /// <summary>
        /// 自定義：海外商品 (非券商原生 0-6，用於內部標記，例如海期)
        /// </summary>
        Foreign = 99
    }
}

