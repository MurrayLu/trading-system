using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Dtos.Trading; // For OrderIntent if needed, but we use List<OrderIntent>?

namespace MuNTrade.Abstractions.Events.Strategy
{
    /// <summary>
    /// 機構級穩定契約事件：作為策略對外輸出的統一載體。
    /// </summary>
    public sealed record StrategyPacketEmittedEvent(
        StrategyTemplateId TemplateId,
        StrategyInstanceId InstanceId,
        StrategyScope Scope,
        string? ScopeKey, // e.g. "2330" or "Universe:TWHot"
        StrategyCategory Category,
        StrategySeverity Severity,

        // Payload (Expandable)
        Dictionary<string, double>? Indicators = null,
        Dictionary<string, string>? States = null,
        Dictionary<string, string>? Tags = null,
        string? HumanReason = null,
        string? ActionCode = null,
        List<OrderIntent>? Intents = null, // Future replacement for StrategyIntentEmittedEvent
        string? Message = null,

        DateTimeOffset OccurredAt = default,
        string? CorrelationId = null
    ) : IMuEvent;
}

