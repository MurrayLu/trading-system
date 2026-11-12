namespace MuNTrade.Abstractions.Events.System
{
    public sealed record TickHistoryRecordingProgressEvent(
        string Phase,
        string? Symbol,
        int Index,
        int Total,
        string? Message,
        bool IsRunning);
}

