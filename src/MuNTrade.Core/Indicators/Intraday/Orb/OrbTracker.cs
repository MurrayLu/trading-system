using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Indicators;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;

namespace MuNTrade.Core.Indicators.Intraday.Orb
{
    public sealed class OrbTracker : IOrbTracker
    {
        private readonly ConcurrentDictionary<string, Day> _state = new();
        private readonly TimeSpan _start, _end;
        // Night session time range.
        private readonly TimeSpan _nightStart, _nightEnd;

        public OrbTracker(IOptions<OrbSettings> opt)
        {
            var s = opt?.Value ?? new OrbSettings();
            _start = TimeSpan.Parse(s.Start);
            _end = TimeSpan.Parse(s.End);

            // Use safe defaults when night session times are missing or invalid.
            if (!TimeSpan.TryParse(s.NightStart, out _nightStart)) _nightStart = TimeSpan.FromHours(15);
            if (!TimeSpan.TryParse(s.NightEnd, out _nightEnd)) _nightEnd = TimeSpan.FromHours(15.25); // 15:15
        }

        public void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal)
        {
            var d = _state.GetOrAdd(symbol, _ => new Day(tradingDateLocal.Date));

            lock (d)
            {
                // Reset the opening range when the trading date changes.
                if (d.TradingDate.Date != tradingDateLocal.Date) d.Reset(tradingDateLocal.Date);

                var tod = tsLocal.TimeOfDay;

                bool isDayOrb = tod >= _start && tod <= _end;
                bool isNightOrb = tod >= _nightStart && tod <= _nightEnd;

                if (isDayOrb || isNightOrb)
                {
                    if (d.High == 0)
                    {
                        d.High = last;
                        d.Low = last;
                    }
                    else
                    {
                        d.High = Math.Max(d.High, last);
                        d.Low = Math.Min(d.Low, last);
                    }
                }

                // Mark the range closed between sessions and after the night session.
                if ((tod > _end && tod < _nightStart) || (tod > _nightEnd))
                {
                    d.Closed = true;
                }
                else
                {
                    // Keep the range open during either opening range.
                    d.Closed = false;
                }
            }
        }

        public OrbSnapshot? Get(string symbol)
        {
            if (_state.TryGetValue(symbol, out var d))
            {
                lock (d)
                {
                    return new OrbSnapshot(d.TradingDate, d.High, d.Low, d.Closed);
                }
            }
            return null;
        }

        public void Reset(string symbol, DateTime tradingDateLocal)
        {
            var d = _state.GetOrAdd(symbol, _ => new Day(tradingDateLocal.Date));
            lock (d)
            {
                d.Reset(tradingDateLocal.Date);
            }
        }

        private sealed class Day
        {
            public Day(DateTime date) => Reset(date);
            public DateTime TradingDate { get; private set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public bool Closed { get; set; }
            public void Reset(DateTime date) { TradingDate = date.Date; High = 0; Low = 0; Closed = false; }
        }
    }
}
