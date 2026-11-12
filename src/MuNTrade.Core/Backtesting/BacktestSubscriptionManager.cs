using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.System;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest 版 SubscriptionManager：維護 tick 訂閱清單，不做實際券商連線。
    /// </summary>
    public sealed class BacktestSubscriptionManager : ISubscriptionManager
    {
        private readonly HashSet<string> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ticks = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();
        private readonly IEventBus _bus;
        private readonly IClock _clock;

        private string? _exclusiveOwner;

        public BacktestSubscriptionManager(IEventBus bus, IClock clock)
        {
            _bus = bus;
            _clock = clock;
        }

        public IReadOnlyCollection<string> Current => _quotes;
        public IReadOnlyCollection<string> CurrentTickSubscriptions => _ticks;

        public void SubscribeQuote(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            lock (_sync) _quotes.Add(symbol);
        }

        public void SubscribeTicks(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            lock (_sync) _ticks.Add(symbol);
            _bus.Publish(new TicksSubscribedEvent
            {
                Symbol = symbol,
                OccurredAt = ToSafeOffset(_clock.Now)
            });
        }

        public Task<bool> SubscribeTicksAsync(string symbol, CancellationToken ct = default)
        {
            SubscribeTicks(symbol);
            return Task.FromResult(true);
        }

        public void UnsubscribeTicks(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            lock (_sync) _ticks.Remove(symbol);
            _bus.Publish(new TicksUnsubscribedEvent
            {
                Symbol = symbol,
                OccurredAt = ToSafeOffset(_clock.Now)
            });
        }

        public Task<bool> UnsubscribeTicksAsync(string symbol, CancellationToken ct = default)
        {
            UnsubscribeTicks(symbol);
            return Task.FromResult(true);
        }

        public bool Unsubscribe(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            lock (_sync)
            {
                _quotes.Remove(symbol);
                _ticks.Remove(symbol);
            }
            return true;
        }

        public Task<bool> UnsubscribeAsync(string symbol, CancellationToken ct = default)
        {
            return Task.FromResult(Unsubscribe(symbol));
        }

        public bool TryGetInitialState(string symbol, [NotNullWhen(true)] out MuNTrade.Abstractions.Events.System.SymbolStaticDataLoadedEvent? state)
        {
            state = null;
            return false;
        }

        public IDisposable BeginExclusiveTickSubscription(string owner)
        {
            lock (_sync) _exclusiveOwner = owner;
            return new ExclusiveScope(this);
        }

        public Task<bool> SubscribeTicksExclusiveAsync(string symbol, CancellationToken ct = default)
        {
            // Backtest 不做獨佔限制，直接視為成功。
            SubscribeTicks(symbol);
            return Task.FromResult(true);
        }

        public void RefreshQuotesAndTicks(IReadOnlyCollection<string>? quoteSymbols = null, IReadOnlyCollection<string>? tickSymbols = null)
        {
            // Backtest no-op
        }

        public Task RefreshQuotesAndTicksAsync(IReadOnlyCollection<string>? quoteSymbols = null, IReadOnlyCollection<string>? tickSymbols = null)
            => Task.CompletedTask;

        public void RefreshStaticData(IReadOnlyCollection<string>? symbols = null, string reason = "Unknown", bool includeUnsubscribed = false)
        {
            // Backtest no-op
        }

        public Task RefreshStaticDataAsync(IReadOnlyCollection<string>? symbols = null, string reason = "Unknown", bool includeUnsubscribed = false)
            => Task.CompletedTask;

        public Task<bool> SubscribeQuoteAsync(string symbol, CancellationToken ct = default)
        {
            SubscribeQuote(symbol);
            return Task.FromResult(true);
        }

        private sealed class ExclusiveScope : IDisposable
        {
            private readonly BacktestSubscriptionManager _mgr;
            private bool _disposed;

            public ExclusiveScope(BacktestSubscriptionManager mgr) => _mgr = mgr;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                lock (_mgr._sync) _mgr._exclusiveOwner = null;
            }
        }

        private static DateTimeOffset ToSafeOffset(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return DateTimeOffset.MinValue;
            }

            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }
    }
}

