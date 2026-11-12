using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public class PositionDto
    {
        public string Symbol { get; set; } = string.Empty;
        public OrderSide Side { get; set; }           // 多/空方向
        public long Quantity { get; set; }            // 數量 (股數或口數)
        public decimal AverageCost { get; set; }     // 平均成本
        public decimal MarketValue { get; set; }     // 即時市值
        public decimal LastPrice { get; set; }       // 市場現價
        public decimal UnrealizedPnL { get; set; }   // 未實現損益
        public decimal UnrealizedPnLRatio { get; set; } // 未實現損益率 (%)

        // 成本與損平
        public decimal EstimatedTotalCost { get; set; }
        public decimal BreakEvenPrice { get; set; }

        //策略相關欄位 (由我們的系統內部計算和維護) ---
        public decimal? EstimatedStopLoss { get; set; } // 策略計算出的預估停損點
        public decimal? ProfitTarget { get; set; }      // 策略計算出的預期獲利點

        // 帳戶 (用於區分模擬/實盤)
        public string Account { get; set; } = string.Empty;

        // Unified Architecture Keys
        public string AccountKey { get; set; } = string.Empty;

        // Market Context (For UI/Logging correctness)
        public MarketNo MarketNo { get; set; }
        public MarketSource Source { get; set; }
    }
}
