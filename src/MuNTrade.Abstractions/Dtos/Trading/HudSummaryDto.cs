using System;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed class LatencySlippageStats
    {
        public int P50LatencyMs { get; init; }
        public int P95LatencyMs { get; init; }
        public decimal P50Slippage { get; init; }
        public decimal P95Slippage { get; init; }
    }

    public sealed class HudSummaryDto
    {
        public required string TradingMode { get; init; }     // PAPER/LIVE
        public required DateTime TradingDate { get; init; }

        public decimal RealizedPnL { get; init; }
        public decimal UnrealizedPnL { get; init; }
        public decimal Equity { get; init; }
        public decimal MaxDrawdown { get; init; }
        public decimal MaxDrawdownPct { get; init; }

        public decimal BuyingPowerUtilStock { get; init; }
        public decimal BuyingPowerUtilFutures { get; init; }

        public int TradesUsed { get; init; }
        public int TradesLimit { get; init; }
        public int SymbolsActive { get; init; }

        public LatencySlippageStats Exec { get; init; } = new();
    }
}

