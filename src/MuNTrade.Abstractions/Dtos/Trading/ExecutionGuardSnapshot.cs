namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record ExecutionGuardSnapshot(
        bool IsPass,
        int SpreadTicks,
        int BestAskDepth,
        int BestBidDepth,
        decimal AvwapDeltaPct,
        decimal OfiShortTerm,
        int MinutesFromOpen,
        string? RejectReason = null
    );
}
