using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Events.System
{
    /// <summary>
    /// 當一個商品的初始靜態資料被成功載入後發布的事件。
    /// </summary>
    /// <summary>
    /// 當一個商品的初始靜態資料被成功載入後發布的事件。
    /// </summary>
    public class SymbolStaticDataLoadedEvent : MuNTrade.Abstractions.Contracts.Eventing.IMuEvent, MuNTrade.Abstractions.Contracts.Market.IMarketStamped
    {
        public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;
        public string? CorrelationId { get; init; }

        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public MuNTrade.Abstractions.Domain.Common.Enums.MarketNo MarketNo { get; set; }
        public MuNTrade.Abstractions.Domain.Common.Enums.MarketSource Source { get; set; } = MuNTrade.Abstractions.Domain.Common.Enums.MarketSource.Domestic;
        public decimal ReferencePrice { get; set; }
        public long YesterdayVolume { get; set; }
        public decimal DayHigh { get; set; }
        public decimal DayLow { get; set; }
        public long TotalVolume { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal CeilingPrice { get; set; }
        public decimal FloorPrice { get; set; }
        public string Industry { get; set; } = string.Empty; // 產業別 (未來可擴充)
        public DayTradeStatus? DayTradeStatus { get; set; }
        public bool IsAlerted { get; set; }                // 是否為注意/警示股 (未來可擴充)
        public bool IsDisposal { get; set; }               // 是否為處置股 (未來可擴充)

        // ★★★ 新增資券與當沖欄位 (Phase 2 Requirement) ★★★
        public double MarginRatio { get; set; }            // 融資成數 (e.g. 60)
        public double ShortRatio { get; set; }             // 融券成數 (e.g. 90)
        public bool IsDayTradeAvailable { get; set; }      // 是否可現沖

        // ===== 資券配額/交易限制 (一次性解析，避免重複改動索引) =====
        public bool IsMarginDisabled { get; set; }         // 融資標記 (停資/取消信用)
        public int MarginQuota { get; set; }               // 融資限量
        public bool IsShortDisabled { get; set; }          // 融券標記 (停券/取消信用)
        public int ShortQuota { get; set; }                // 融券限量
        public bool IsDowngraded { get; set; }             // 被降成標記
        public bool CanShortOnFlat { get; set; }           // 平盤放空
        public bool IsFullDelivery { get; set; }           // 全額交割
        public bool IsWarning { get; set; }                // 公司內部警示記號
        public bool IsNotice { get; set; }                 // 注意股票
        public bool IsRestricted { get; set; }             // 受限股票
        public bool IsAbnormal { get; set; }               // 異常/特殊異常
        public bool IsElasticDisposal { get; set; }        // 彈性處置 (處置標記=3)
        public int MaxQtyPerOrder { get; set; }            // 單筆委託張數限制
        public int MaxQtyMultiOrder { get; set; }          // 多筆委託張數限制
        public double PreCollectionRatio { get; set; }     // 款券預收成數
        public bool IsPreCollection { get; set; }          // 是否需款券預收

        // ★★★ 這是本次需要新增的關鍵欄位 ★★★
        /// <summary>
        /// 價格最小跳動單位 (Tick)。
        /// 注意：對於台股，這應該是動態的。這裡可以放一個初始值或最常見的值。
        /// 更精確的做法是 Builder 內部根據價格再用價階表計算。
        /// </summary>
        public decimal TickPrice { get; set; }

        /// <summary>
        /// 每張股數 (例如：1000)。
        /// </summary>
        public int LotSize { get; set; }

        /// <summary>
        /// 價格小數位數。
        /// </summary>
        public int PriceDecimals { get; set; }
    }
}

