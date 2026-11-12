using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    //訊號 & 驗證 ---
    /// <summary>
    /// 候選交易：由策略偵測到潛在進場訊號時產生，尚未經過守門員與驗證。
    /// </summary>
    public sealed record TradeCandidate(
        string Symbol,
        OrderSide Side,
        string SignalType,  // Signal source, e.g. "MomentumContinuation_Long"
        ArrivalTs DetectedAt,
        decimal TriggerPrice,
        decimal? StructuralStop,
        TrendDirection Trend5m,
        TrendDirection Trend15m,
        TrendDirection Trend60m,
        PullbackQuality PullbackQuality,

        // Candidate fields displayed in the UI.
        decimal? Score = null, // 單一數值分數（尚未細分三維度）
        string? Tag = null     // 額外標籤（例如 "GapUp", "VWAPCross"）
    );
}
