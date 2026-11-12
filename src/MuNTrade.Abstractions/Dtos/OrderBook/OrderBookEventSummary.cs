using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.OrderBook
{
    // 每分鐘/區段的偵測摘要
    public record OrderBookEventSummary(
        string Symbol,
        ArrivalTs WindowEnd,
        int SuspectCount,
        int ConfirmedCount,
        int CumHangCount,
        int CumPullCount,
        int CumReplenishCount,
        int BigHangCount,
        int BigPullCount,
        int LevelsBid,
        int LevelsAsk,
        int Evictions,
        double StaleRatio,
        int MatchedSamples,
        int ReplenishSamples,
        int RatioSamples,
        int TouchSamples
    );
}
