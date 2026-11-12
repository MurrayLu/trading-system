using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Core.Capital;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Session;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest only: update DailyLossUsed from CompletedTrade (loss only).
    /// </summary>
    public sealed class BacktestRiskCounterService : IDisposable
    {
        private const string FuturesSessionSymbol = "TX00";
        private readonly IEventBus _bus;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IMarketInfoProvider _marketInfo;
        private readonly IClock _clock;
        private readonly ILogger<BacktestRiskCounterService> _logger;
        private readonly IDisposable _token;

        public BacktestRiskCounterService(
            IEventBus bus,
            ITradingSessionProvider sessionProvider,
            IMarketInfoProvider marketInfo,
            IClock clock,
            ILogger<BacktestRiskCounterService> logger)
        {
            _bus = bus;
            _sessionProvider = sessionProvider;
            _marketInfo = marketInfo;
            _clock = clock;
            _logger = logger;
            _token = _bus.Subscribe<CompletedTrade>(OnCompletedTrade);
        }

        private void OnCompletedTrade(CompletedTrade trade)
        {
            if (trade == null || string.IsNullOrWhiteSpace(trade.StockId)) return;
            if (trade.NetPnL >= 0) return;

            var tradeDate = ResolveTradeDate(trade.ExitTime == default ? _clock.Now : trade.ExitTime, out var source);

            var accounts = CapitalConfigService.LoadAllSimAccounts();
            if (accounts == null || accounts.Count == 0)
            {
                _logger.LogWarning("[BacktestRisk] No sim account found for daily loss update.");
                return;
            }

            var accountKey = ResolveAccountKey(trade);
            var acc = ResolveSimAccount(accounts, accountKey);
            if (acc == null)
            {
                _logger.LogWarning("[BacktestRisk] Sim account not found. accountKey={AccountKey}", accountKey);
                return;
            }

            var info = _marketInfo.GetMarketInfoOrThrow(trade.StockId, MarketInfoPurpose.Risk);
            var loss = Math.Abs(trade.NetPnL);

            if (info.Kind == MarketKind.Derivatives)
            {
                if (acc.FuturesDailyLossTradeDate != tradeDate)
                {
                    _logger.LogWarning("[TradeDate][Mismatch] component=BacktestRisk mkt=Futures tradeDate={TradeDate} frozenDate={FrozenDate} source={Source}",
                        tradeDate.ToString("yyyy-MM-dd"), acc.FuturesDailyLossTradeDate.ToString("yyyy-MM-dd"), source);
                    return;
                }

                acc.FuturesDailyLossUsedFrozen += loss;
                _logger.LogInformation("[BacktestRisk] FuturesDailyLossUsed +{Loss:N0} => {Total:N0} tradeDate={TradeDate}",
                    loss, acc.FuturesDailyLossUsedFrozen, tradeDate.ToString("yyyy-MM-dd"));
            }
            else
            {
                if (acc.StockDailyLossTradeDate != tradeDate)
                {
                    _logger.LogWarning("[TradeDate][Mismatch] component=BacktestRisk mkt=Stock tradeDate={TradeDate} frozenDate={FrozenDate} source={Source}",
                        tradeDate.ToString("yyyy-MM-dd"), acc.StockDailyLossTradeDate.ToString("yyyy-MM-dd"), source);
                    return;
                }

                acc.StockDailyLossUsedFrozen += loss;
                _logger.LogInformation("[BacktestRisk] StockDailyLossUsed +{Loss:N0} => {Total:N0} tradeDate={TradeDate}",
                    loss, acc.StockDailyLossUsedFrozen, tradeDate.ToString("yyyy-MM-dd"));
            }

            CapitalConfigService.SaveSimAccount(acc, _clock);
        }

        private DateOnly ResolveTradeDate(DateTime now, out string source)
        {
            var session = _sessionProvider.GetSession(FuturesSessionSymbol, now);
            if (session != null)
            {
                source = "session";
                return session.TradeDate;
            }

            source = "fallback";
            var td = now.TimeOfDay >= new TimeSpan(15, 0, 0) ? now.Date.AddDays(1) : now.Date;
            return DateOnly.FromDateTime(td);
        }

        private static string ResolveAccountKey(CompletedTrade trade)
        {
            if (!string.IsNullOrWhiteSpace(trade.AccountKey) &&
                !trade.AccountKey.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return trade.AccountKey;
            }
            if (!string.IsNullOrWhiteSpace(trade.Account)) return trade.Account;
            return "Sim:Paper:All:Default";
        }

        private static SimAccountModel? ResolveSimAccount(
            System.Collections.Generic.List<SimAccountModel> accounts,
            string accountKey)
        {
            var acc = accounts.FirstOrDefault(a => string.Equals(a.Id, accountKey, StringComparison.OrdinalIgnoreCase));
            if (acc != null) return acc;

            bool isDefaultKey = accountKey.Contains("Default", StringComparison.OrdinalIgnoreCase)
                             || accountKey.Contains("Paper", StringComparison.OrdinalIgnoreCase);
            if (isDefaultKey)
            {
                acc = accounts.FirstOrDefault(a => a.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)
                                               || a.Name.Contains("Paper", StringComparison.OrdinalIgnoreCase));
                if (acc != null) return acc;
            }

            return accounts.Count == 1 ? accounts[0] : accounts.FirstOrDefault(a => a.IsActive) ?? accounts.FirstOrDefault();
        }

        public void Dispose()
        {
            _token.Dispose();
        }
    }
}

