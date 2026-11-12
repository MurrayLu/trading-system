using System;
using MuNTrade.Abstractions.Contracts.Eventing;

namespace MuNTrade.Abstractions.Events.Strategy
{
    /// <summary>
    /// 供 UI/控制層使用：目前是否有策略在 Running（與數量）。
    /// StrategyUiStore 會在狀態變化時發佈。
    /// </summary>
    public sealed record StrategyRunningSummaryChangedEvent(
        bool HasRunning,
        int RunningCount,
        DateTimeOffset OccurredAt,
        string? CorrelationId = null
    ) : IMuEvent;
}

