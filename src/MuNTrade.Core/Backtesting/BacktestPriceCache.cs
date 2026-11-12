using System.Collections.Concurrent;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Events.MarketData;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest 用的最新價格快取：由 TradeTickEvent 驅動。
    /// </summary>
    public sealed class BacktestPriceCache
    {
        private readonly ConcurrentDictionary<string, decimal> _last = new(StringComparer.OrdinalIgnoreCase);

        public BacktestPriceCache(IEventBus bus)
        {
            bus.Subscribe<TradeTickEvent>(OnTick);
        }

        private void OnTick(TradeTickEvent tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Symbol)) return;
            _last[tick.Symbol] = tick.Price;
        }

        public bool TryGetLastPrice(string symbol, out decimal price)
            => _last.TryGetValue(symbol, out price);
    }
}

