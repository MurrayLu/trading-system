using System;
using MuNTrade.Abstractions.Contracts.Eventing;

namespace MuNTrade.Abstractions.Events.System
{
    /// <summary>
    /// UI/系統層的「模式切換」宣告事件。
    /// 注意：這不代表 DI/下單通道已熱切換，僅代表使用者意圖與 UI 狀態切換。
    /// </summary>
    public sealed record GatewayModeChangedEvent(
        string Mode,                      // "PAPER" | "LIVE"
        DateTimeOffset OccurredAt,
        string? CorrelationId = null
    ) : IMuEvent;
}

