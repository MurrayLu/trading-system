// MuNTrade.Abstractions/Interfaces/Indicators/IIndicatorCalculator.cs
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
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Indicators
{
    public interface IIndicatorCalculator
    {
        // 讓 Warmup 直接灌入歷史，ATR/SMA 立即可用
        void OnIndicatorInitialize(IndicatorInitializeRequest req);

        // ── K-Line Based ──
        void OnKLinePartial(KLine bar, IEnumerable<string>? enabledIndicators = null);
        void OnKLineClosed(KLine bar, IEnumerable<string>? enabledIndicators = null);

        // ── Tick/Quote Based ──
        void OnTick(TradeTickEvent tick, IEnumerable<string>? enabledIndicators = null);
        void OnBest5(QuoteBest5Event best5, IEnumerable<string>? enabledIndicators = null);

    }
}

