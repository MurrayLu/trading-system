using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;

namespace MuNTrade.Core.Trading.Adapters
{
    public sealed class ProfitLossGWReportTranslator
    {
        private readonly IEventBus _bus;
        private readonly ILogger<ProfitLossGWReportTranslator> _logger;
        private readonly IClock _clock;

        public ProfitLossGWReportTranslator(IEventBus bus, ILogger<ProfitLossGWReportTranslator> logger, IClock clock)
        {
            _bus = bus;
            _logger = logger;
            _clock = clock;
            _bus.Subscribe<ProfitLossGWReportEvent>(OnReport);
        }

        private void OnReport(ProfitLossGWReportEvent evt)
        {
            if (evt.ResultCode != "000") return;
            if (evt.Context?.QueryType != ProfitLossQueryType.Unrealized) return;
            if (evt.Context?.Format != ProfitLossFormat.Summary) return;

            var metrics = new List<PositionMetricDto>();

            foreach (var row in evt.Rows)
            {
                var f = row.Fields;
                if (f.Count < 30) continue;

                var symbol = f[1].Trim();            // 2 股票代號
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                var qty = ParseLong(f[4]);           // 5 庫存股數
                var mkt = ParseDec(f[5]);            // 6 市價
                var pnl = ParseDec(f[9]);            // 10 損益
                var avg = ParseDec(f[10]);           // 11 平均成本
                var fee = ParseDec(f[13]);           // 14 手續費
                var tax = ParseDec(f[15]);           // 16 交易稅
                var bep = ParseDec(f[27]);           // 28 損益兩平點
                var ratio = ParseDec(f[21]);         // 22 試算報酬率
                var account = f[29].Trim();          // 30 ACCOUNT_NO

                metrics.Add(new PositionMetricDto
                {
                    Account = account,
                    Symbol = symbol,
                    Quantity = qty,
                    MarketPrice = mkt,
                    AverageCost = avg,
                    UnrealizedPnL = pnl,
                    BreakEvenPrice = bep,
                    Commission = fee,
                    Tax = tax,
                    PnLRatio = ratio
                });
            }

            if (metrics.Count == 0) return;

            _bus.Publish(new BrokerPositionMetricsEvent
            {
                Metrics = metrics,
                OccurredAt = new DateTimeOffset(_clock.Now)
            });
        }

        private static decimal ParseDec(string s) => decimal.TryParse(s, out var v) ? v : 0m;
        private static long ParseLong(string s) => long.TryParse(s, out var v) ? v : 0;
    }
}

