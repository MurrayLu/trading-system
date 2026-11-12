using System;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Helpers
{
    /// <summary>
    /// 市場分類器與擴充方法，提供唯一分類依據。
    /// </summary>
    public static class MarketClassifier
    {
        /// <summary>
        /// 將 MarketNo 轉換為大分類 (Securities/Derivatives)
        /// </summary>
        public static MarketKind ToKind(this MarketNo marketNo)
        {
            return marketNo switch
            {
                MarketNo.Listing or
                MarketNo.OTC or
                MarketNo.Emerging or
                MarketNo.IntradayOddListing or
                MarketNo.IntradayOddOTC => MarketKind.Securities,

                MarketNo.Futures or
                MarketNo.Options => MarketKind.Derivatives,

                // 海外商品 (MarketNo=99) 一般歸類為衍生品 (海期)
                // 若未來有海外證券需求，需搭配 Source/Producer 進一步判斷，但在 MarketNo 層級暫歸 Derivatives
                MarketNo.Foreign => MarketKind.Derivatives,

                _ => MarketKind.Unknown
            };
        }

        public static bool IsSecurities(this MarketNo marketNo)
        {
            return marketNo.ToKind() == MarketKind.Securities;
        }

        public static bool IsDerivatives(this MarketNo marketNo)
        {
            return marketNo.ToKind() == MarketKind.Derivatives;
        }

        public static bool IsOverseas(this MarketNo marketNo)
        {
            return marketNo == MarketNo.Foreign;
        }

        public static string ToDisplay(this MarketNo marketNo)
        {
            return marketNo switch
            {
                MarketNo.Listing => "上市",
                MarketNo.OTC => "上櫃",
                MarketNo.Futures => "期貨",
                MarketNo.Options => "選擇權",
                MarketNo.Emerging => "興櫃",
                MarketNo.IntradayOddListing => "上市零股",
                MarketNo.IntradayOddOTC => "上櫃零股",
                MarketNo.Foreign => "海外",
                _ => "未知"
            };
        }
    }
}

