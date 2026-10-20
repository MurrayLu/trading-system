// 檔案路徑: MuNTrade.Abstractions/Domain/Models/OrderRequest.cs
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 【升級後版本】
    /// 代表一筆由 PositionManager 計算完畢，準備送往風控和執行的訂單請求。
    /// </summary>
    public class OrderRequest
    {
        /// <summary>
        /// 觸發此請求的原始訊號。
        /// </summary>
        public required SignalEvent OriginalSignal { get; set; }

        /// <summary>
        /// 【新增】明確的買賣方向。
        /// </summary>
        public OrderSide Side { get; set; }

        /// <summary>
        /// 【修改】這是計算後需要執行的「差異」數量。
        /// </summary>
        public long Quantity { get; set; }

        public decimal StopLossPrice { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public ExecutionTrace? Trace { get; set; }  // 第1版容忍 null；第2版可升為 required

        /// <summary>
        /// 是否為實單交易 (Live Trading)。
        /// </summary>
        public bool IsLive { get; set; }

        // --- Advanced Order Fields ---
        public TimeInForce TimeInForce { get; set; }
        public OrderType OrderType { get; set; }
        public TradeType TradeType { get; set; }
        public bool IsDayTrade { get; set; }
        public bool IsMitOrder { get; set; }
        public decimal MitTriggerPrice { get; set; }

        public string? StrategyId { get; set; }
        public string? CorrelationId { get; set; }
        public string? AccountKey { get; set; }

        /// <summary>
        /// [10C] Explicit MarketKind (Pinned Truth)
        /// </summary>
        public MarketKind MarketKind { get; set; } = MarketKind.Unknown;
    }
}

