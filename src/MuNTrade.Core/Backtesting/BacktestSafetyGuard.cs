using System;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Settings.Common;

namespace MuNTrade.Core.Backtesting
{
    public static class BacktestSafetyGuard
    {
        public static void EnsurePaperOnly(ApplicationSettings settings)
        {
            if (settings == null)
                throw new InvalidOperationException("ApplicationSettings is missing.");

            if (settings.AppMode is not (AppMode.Backtest or AppMode.GovernedDaytradeBatch))
                throw new InvalidOperationException($"Backtest requires AppMode=Backtest or GovernedDaytradeBatch (current={settings.AppMode}).");

            var gateway = settings.TradingGateway?.Trim() ?? "PAPER";
            if (string.Equals(gateway, "LIVE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backtest requires PAPER ONLY. TradingGateway=LIVE is not allowed.");
        }

        public static void EnsureBacktestGateway(IOrderGateway gateway)
        {
            if (gateway == null)
                throw new InvalidOperationException("Order gateway is missing.");

            if (gateway is not BacktestOrderGateway)
                throw new InvalidOperationException($"Backtest requires BacktestOrderGateway. Actual={gateway.GetType().Name}");
        }
    }
}

