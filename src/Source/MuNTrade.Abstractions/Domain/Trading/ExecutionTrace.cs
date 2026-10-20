using System;

namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 送單當刻的執行意圖戳記，用於全鏈路關聯與執行品質計算。
    /// </summary>
    public sealed class ExecutionTrace
    {
        public required string CorrelationId { get; init; }      // 建議 Guid.NewGuid("N")
        public required DateTime IntentTimestamp { get; init; }  // 系統內一致(建議本地時間)，LIVE 可用 IClock.Now
        public decimal? DecisionPrice { get; init; }             // 下單決策價（Intent 當下快照）

        public string StrategyId { get; init; } = "Manual";
        public string AccountKey { get; init; } = "Synthetic:Paper:Demo";
        public string? Origin { get; init; } // "UI"/"Strategy"/"System"
        public string? Symbol { get; init; }
        public MuNTrade.Abstractions.Domain.Trading.Enums.OrderSide? Side { get; init; }
        public long? Quantity { get; init; }
    }
}

