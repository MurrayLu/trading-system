namespace MuNTrade.Abstractions.Dtos.Trading
{
    // 成交品質守門員數據
    public sealed record GuardMetrics(
        string Symbol,
        int SpreadTicks,
        int BidDepth,
        int AskDepth,
        decimal Ofi,
        int MinutesFromOpen,
        bool IsAuctionOrVI,
        bool IsPass
    );
}
