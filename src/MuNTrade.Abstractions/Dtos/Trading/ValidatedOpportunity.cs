using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    /// <summary>
    /// 驗證後的進場機會：已經通過守門員檢查，並具備風控參數。
    /// 同時包含 UI 需要的分數/門檻/建議單量等資訊。
    /// </summary>
    public sealed record ValidatedOpportunity(
        TradeCandidate Candidate,
        ArrivalTs ValidatedAt,
        int ConfidenceScore,
        bool Go,                          // 是否允許進場
        RiskParams Risk,
        ExecutionGuardSnapshot Guard,
        ValidationDetails Details,

        // === UI 展示用屬性 ===
        string Symbol = null!,            // Candidate.Symbol
        bool IsApproved = false,          // = Go
        string? Reason = null,
        decimal? ScoreTrend = null,
        decimal? ScorePullback = null,
        decimal? ScoreBreakout = null,
        decimal? ThTrend = null,
        decimal? ThPullback = null,
        decimal? ThBreakout = null,
        decimal? Entry = null,
        decimal? Stop = null,
        decimal? R1 = null,
        decimal? R2 = null,
        int? SuggestedQty = null
    );
}
