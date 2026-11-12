using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    // Optional. Old strategies don't implement it => receive all subscribed types.
    public interface IStrategyIngressSpec
    {
        // e.g. ["TX00"] ; empty/null => no symbol filtering
        IReadOnlyCollection<string>? Symbols { get; }

        // e.g. ["Time_1", "Time_5"] ; empty/null => no period filtering
        IReadOnlyCollection<string>? Periods { get; }

        // e.g. ["SMA_5", "SMA_10"] ; empty/null => no indicator-key filtering
        IReadOnlyCollection<string>? IndicatorKeys { get; }
    }
}

