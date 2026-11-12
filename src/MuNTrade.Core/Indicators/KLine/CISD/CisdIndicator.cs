using MuNTrade.Abstractions.Contracts.Indicators;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Core.Indicators.KLine; // For IndicatorSeriesPoint

namespace MuNTrade.Core.Indicators.KLine.CISD
{
    public sealed class CisdIndicator : IKLineIndicator
    {
        public string Name => "CISD";

        // State storage: Symbol -> Timeframe -> LogicInstance
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CisdLogic>> _states
            = new(StringComparer.OrdinalIgnoreCase);

        public CisdIndicator()
        {
        }

        public IReadOnlyDictionary<string, IReadOnlyList<IndicatorSeriesPoint>> Initialize(
            string symbol, string timeframe, IReadOnlyList<JsonKLineRecord> bars)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            // Re-initialize logic on Initialize call
            var logic = new CisdLogic();
            tfDict[timeframe] = logic;

            // Ensure bars are ordered Oldest -> Newest
            IReadOnlyList<JsonKLineRecord> ordered = bars;
            if (bars.Count > 1 && bars[0].KlineTime > bars[^1].KlineTime)
                ordered = bars.Reverse().ToList();

            foreach (var bar in ordered)
            {
                logic.Update((decimal)bar.Open, (decimal)bar.High, (decimal)bar.Low, (decimal)bar.Close);
            }

            return new Dictionary<string, IReadOnlyList<IndicatorSeriesPoint>>();
        }

        public IReadOnlyDictionary<string, decimal> OnClosed(string symbol, string timeframe, decimal open, decimal high, decimal low, decimal close, ExchangeTs ts)
        {
            var logic = GetLogic(symbol, timeframe);
            logic.Update(open, high, low, close);
            return logic.GetOutputs();
        }

        public IReadOnlyDictionary<string, decimal> OnPartial(string symbol, string timeframe, decimal open, decimal high, decimal low, decimal close, ExchangeTs ts)
        {
            var logic = GetLogic(symbol, timeframe);
            // 使用預覽模式，不影響內部狀態，且因優化過，Clone 開銷極低
            return logic.Preview(open, high, low, close);
        }

        private CisdLogic GetLogic(string symbol, string timeframe)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            return tfDict.GetOrAdd(timeframe, _ => new CisdLogic());
        }

        // ==========================================
        // Internal Logic Class (Optimized)
        // ==========================================
        private class CisdLogic
        {
            // Settings
            private const bool KeepLevels = false;

            // Rolling History (Max 3 bars needed for logic: Current, Prev, PrevPrev)
            private struct BarData
            {
                public decimal Open;
                public decimal Close;
                // High/Low not strictly used for body logic, but good for completeness
                public decimal BodyHigh => Math.Max(Open, Close);
                public decimal BodyLow => Math.Min(Open, Close);
                public decimal BodySize => Math.Abs(Close - Open);
            }

            private readonly BarData[] _rollingBars = new BarData[3];
            private int _totalBarsProcessed = 0;

            // Structure
            private struct MarketStructure
            {
                public decimal TopPrice;
                public decimal BottomPrice;
                public bool IsBullish;
            }

            private class CisdLevel
            {
                public decimal LevelPrice;
                public bool Completed;
            }

            // Vars
            private MarketStructure _currentStructure;
            private readonly List<CisdLevel> _cisdLevelsBu = new();
            private readonly List<CisdLevel> _cisdLevelsBe = new();

            private bool _isBullishPullback = false;
            private bool _isBearishPullback = false;

            private decimal _potentialTopPrice = 0;
            private decimal _potentialBottomPrice = 0;

            // ★ Optimization: Store values directly, not indices
            // Bullish Pullback Anchor Values (for creating Bearish Level)
            private decimal _anchorBotLow = 0;
            private decimal _anchorBotPrevLow = 0;

            // Bearish Pullback Anchor Values (for creating Bullish Level)
            private decimal _anchorTopHigh = 0;
            private decimal _anchorTopPrevHigh = 0;

            private bool _currentState = false; // false = bearish, true = bullish

            public CisdLogic()
            {
                _currentStructure = new MarketStructure { TopPrice = 0, BottomPrice = 0, IsBullish = false };
            }

            // Deep Clone for Preview
            public CisdLogic Clone()
            {
                var clone = new CisdLogic();
                clone._totalBarsProcessed = _totalBarsProcessed;
                Array.Copy(_rollingBars, clone._rollingBars, 3);

                clone._currentStructure = _currentStructure;

                // Clone lists
                foreach (var l in _cisdLevelsBu) clone._cisdLevelsBu.Add(new CisdLevel { LevelPrice = l.LevelPrice, Completed = l.Completed });
                foreach (var l in _cisdLevelsBe) clone._cisdLevelsBe.Add(new CisdLevel { LevelPrice = l.LevelPrice, Completed = l.Completed });

                clone._isBullishPullback = _isBullishPullback;
                clone._isBearishPullback = _isBearishPullback;
                clone._potentialTopPrice = _potentialTopPrice;
                clone._potentialBottomPrice = _potentialBottomPrice;

                clone._anchorBotLow = _anchorBotLow;
                clone._anchorBotPrevLow = _anchorBotPrevLow;
                clone._anchorTopHigh = _anchorTopHigh;
                clone._anchorTopPrevHigh = _anchorTopPrevHigh;

                clone._currentState = _currentState;

                return clone;
            }

            public Dictionary<string, decimal> Preview(decimal open, decimal high, decimal low, decimal close)
            {
                var sim = this.Clone();
                sim.Update(open, high, low, close);
                return sim.GetOutputs();
            }

            public void Update(decimal open, decimal high, decimal low, decimal close)
            {
                // Shift rolling buffer
                _rollingBars[2] = _rollingBars[1];
                _rollingBars[1] = _rollingBars[0];
                _rollingBars[0] = new BarData { Open = open, Close = close };

                _totalBarsProcessed++;

                // Initialization check
                if (_totalBarsProcessed == 1)
                {
                    _currentStructure = new MarketStructure
                    {
                        TopPrice = Math.Max(open, close),
                        BottomPrice = Math.Min(open, close),
                        IsBullish = close >= open
                    };
                    return;
                }

                // Need at least 3 bars to safely check [1] and [2]
                if (_totalBarsProcessed < 3) return;

                // Accessors
                // 0 = Current, 1 = Prev, 2 = PrevPrev
                ref var bar0 = ref _rollingBars[0];
                ref var bar1 = ref _rollingBars[1];
                ref var bar2 = ref _rollingBars[2];

                // ========================
                // Pullback Detection
                // ========================
                decimal minPullbackRatio = 0.5m;

                decimal pullbackBody = bar1.BodySize;
                decimal prevBody = bar2.BodySize;

                bool validPullback = prevBody > 0 && pullbackBody >= (minPullbackRatio * prevBody);

                bool bar1Green = bar1.Close > bar1.Open;
                bool bar1Red = bar1.Close < bar1.Open;

                bool bearishPullbackDetected = validPullback && bar1Green;
                bool bullishPullbackDetected = validPullback && bar1Red;

                // Bearish Pullback Logic
                if (bearishPullbackDetected && !_isBearishPullback)
                {
                    _isBearishPullback = true;
                    _potentialTopPrice = bar1.Open;
                    // Cache anchor values (Bar1 is anchor, Bar2 is prev)
                    _anchorTopHigh = bar1.BodyHigh;
                    _anchorTopPrevHigh = bar2.BodyHigh;
                }

                // Bullish Pullback Logic
                if (bullishPullbackDetected && !_isBullishPullback)
                {
                    _isBullishPullback = true;
                    _potentialBottomPrice = bar1.Open;
                    // Cache anchor values
                    _anchorBotLow = bar1.BodyLow;
                    _anchorBotPrevLow = bar2.BodyLow;
                }

                // Update Potential Levels
                if (_isBullishPullback)
                {
                    // New lower body -> update bottom anchor
                    if (bar0.Open < _potentialBottomPrice)
                    {
                        _potentialBottomPrice = bar0.Open;
                        _anchorBotLow = bar0.BodyLow;
                        _anchorBotPrevLow = bar1.BodyLow;
                    }

                    // Red candle with higher open than current bottom -> shift anchor
                    if (bar0.Close < bar0.Open && bar0.Open > _potentialBottomPrice)
                    {
                        _potentialBottomPrice = bar0.Open;
                        _anchorBotLow = bar0.BodyLow;
                        _anchorBotPrevLow = bar1.BodyLow;
                    }
                }

                if (_isBearishPullback)
                {
                    // New higher open -> update top anchor
                    if (bar0.Open > _potentialTopPrice)
                    {
                        _potentialTopPrice = bar0.Open;
                        _anchorTopHigh = bar0.BodyHigh;
                        _anchorTopPrevHigh = bar1.BodyHigh;
                    }
                    // Green candle with lower open than current top -> shift anchor
                    if (bar0.Close > bar0.Open && bar0.Open < _potentialTopPrice)
                    {
                        _potentialTopPrice = bar0.Open;
                        _anchorTopHigh = bar0.BodyHigh;
                        _anchorTopPrevHigh = bar1.BodyHigh;
                    }
                }

                // ========================
                // Structure Updates
                // ========================

                // Bearish Break (Body closes below Bottom)
                if (bar0.BodyLow < _currentStructure.BottomPrice)
                {
                    _currentStructure.BottomPrice = bar0.BodyLow;
                    _currentStructure.IsBullish = false;

                    if (_isBearishPullback) // Assuming distance check implied by state existence
                    {
                        // Use cached anchor highs
                        _currentStructure.TopPrice = Math.Max(_anchorTopHigh, _anchorTopPrevHigh);

                        _isBearishPullback = false;
                        AddBearishLevel(_potentialTopPrice);
                    }
                    else if (bar1.Close > bar1.Open && bar0.Close < bar0.Open)
                    {
                        _currentStructure.TopPrice = bar1.BodyHigh;
                        _isBearishPullback = false;
                        AddBearishLevel(_potentialTopPrice);
                    }
                }

                // Bullish Break (Body closes above Top)
                if (bar0.BodyHigh > _currentStructure.TopPrice)
                {
                    _currentStructure.IsBullish = true;
                    _currentStructure.TopPrice = bar0.BodyHigh;

                    if (_isBullishPullback)
                    {
                        // Use cached anchor lows
                        _currentStructure.BottomPrice = Math.Min(_anchorBotLow, _anchorBotPrevLow);

                        _isBullishPullback = false;
                        AddBullishLevel(_potentialBottomPrice);
                    }
                    else if (bar1.Close < bar1.Open && bar0.Close > bar0.Open)
                    {
                        _currentStructure.BottomPrice = bar1.BodyLow;
                        _isBullishPullback = false;
                        AddBullishLevel(_potentialBottomPrice);
                    }
                }

                // ========================
                // Level Cleanup
                // ========================
                if (_cisdLevelsBu.Count > 1 && !KeepLevels) _cisdLevelsBu.RemoveAt(0);
                if (_cisdLevelsBe.Count > 1 && !KeepLevels) _cisdLevelsBe.RemoveAt(0);

                // ========================
                // Manage CISD Levels
                // ========================
                if (_cisdLevelsBu.Count >= 1)
                {
                    var latestBu = _cisdLevelsBu.Last();
                    // Break: body low closes below CISD
                    if (bar0.BodyLow < latestBu.LevelPrice && !latestBu.Completed)
                    {
                        latestBu.Completed = true;
                        // Spawn new Bearish CISD from current TOP anchor
                        AddBearishLevel(_potentialTopPrice);
                        _currentState = false;
                    }
                }

                if (_cisdLevelsBe.Count >= 1)
                {
                    var latestBe = _cisdLevelsBe.Last();
                    // Break: body high closes above CISD
                    if (bar0.BodyHigh > latestBe.LevelPrice && !latestBe.Completed)
                    {
                        latestBe.Completed = true;
                        // Spawn new Bullish CISD from current BOTTOM anchor
                        AddBullishLevel(_potentialBottomPrice);
                        _currentState = true;
                    }
                }
            }

            private void AddBearishLevel(decimal price)
            {
                // 簡單去重：如果跟上一個一樣，可能不需要重複加？但原邏輯沒擋，這邊照舊
                _cisdLevelsBe.Add(new CisdLevel { LevelPrice = price, Completed = false });
            }

            private void AddBullishLevel(decimal price)
            {
                _cisdLevelsBu.Add(new CisdLevel { LevelPrice = price, Completed = false });
            }

            public Dictionary<string, decimal> GetOutputs()
            {
                var dict = new Dictionary<string, decimal>();

                if (_cisdLevelsBu.Count > 0)
                {
                    var last = _cisdLevelsBu.Last();
                    if (!last.Completed)
                        dict["CISD_Bull_Level"] = last.LevelPrice;
                }

                if (_cisdLevelsBe.Count > 0)
                {
                    var last = _cisdLevelsBe.Last();
                    if (!last.Completed)
                        dict["CISD_Bear_Level"] = last.LevelPrice;
                }

                dict["CISD_State"] = _currentState ? 1m : -1m; // 1=Bull, -1=Bear

                return dict;
            }
        }
    }
}
