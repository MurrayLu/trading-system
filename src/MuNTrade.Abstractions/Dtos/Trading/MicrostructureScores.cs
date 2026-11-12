using System;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    /// <summary>
    /// Scores describing short-term microstructure behaviour for dashboards and analytics.
    /// </summary>
    public sealed record MicrostructureScores(
        string Symbol,
        ArrivalTs WindowStart,
        ArrivalTs WindowEnd,
        double AbsorptionScore,
        int RefillCount,
        int RefillVol,
        double SweepScore,
        int LevelsTraversed,
        double DepletionRate,
        double BuyAggressorProb,
        double SellAggressorProb,
        int MaxFrontWallSize,
        double AvgImbalanceOb
    );
}

