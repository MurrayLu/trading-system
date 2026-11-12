using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Warmup;
using System;
using System.Collections.Generic;

namespace MuNTrade.Infrastructure.DataAccess.Warmup
{
    /// <summary>
    /// 規劃「每個 timeframe 暖機要回傳幾根 (RequiredBars)」與
    /// 「缺口掃描需要覆蓋的天數 (RequiredDays, 以日為單位對 LCD 計)」。
    ///
    /// 設計重點：
    /// 1) 全部 timeframe 的截止日一律以「上一個交易日 LCD」為基準，週/月不採封閉邏輯。
    /// 2) RequiredBars 至少滿足 "max(SMAPeriods) + PaddingBars"，與 MinBars 取較大值。
    /// 3) RequiredDays 可在設定中強制指定；若未指定，使用保守估算（分K按分鐘→天；日/週/月用簡單換算）。
    /// </summary>
    public sealed class WarmupPlanner : IWarmupPlanner
    {
        private readonly WarmupPlannerOptions _opt;

        public WarmupPlanner(IOptions<WarmupPlannerOptions> options)
        {
            _opt = options?.Value ?? new WarmupPlannerOptions();
            _opt.Normalize();
        }

        public IEnumerable<WarmupPlan> GetPlans(string symbol)
        {
            // 規劃順序固定：分K → 日 → 週 → 月
            yield return MakePlan("Time_1", minutesPerBar: 1);
            yield return MakePlan("Time_5", minutesPerBar: 5);
            yield return MakePlan("Time_60", minutesPerBar: 60);
            yield return MakePlan("Day", minutesPerBar: null);
            yield return MakePlan("Week", minutesPerBar: null);
            yield return MakePlan("Month", minutesPerBar: null);
        }

        private WarmupPlan MakePlan(string tf, int? minutesPerBar)
        {
            var tfOpt = _opt.Timeframes[tf];


            // -------------------------
            // ✅ 將 RequiredBars 明確等於 MinBars（不要以 SMA 導出）
            // -------------------------
            var requiredBars = tfOpt.MinBars; // <-- 改成直接用 MinBars


            // RequiredDays：若設定有指定，直接用；否則估算（保留你原先的 day-estimation 行為）
            int requiredDays;
            if (tfOpt.RequiredDays.HasValue && tfOpt.RequiredDays.Value > 0)
            {
                requiredDays = tfOpt.RequiredDays.Value;
            }
            else
            {
                if (minutesPerBar.HasValue)
                {
                    var days = Math.Ceiling(requiredBars * minutesPerBar.Value / (double)_opt.TradingMinutesPerDay);
                    requiredDays = (int)days + _opt.SafetyDays;
                    requiredDays = Math.Max(requiredDays, tfOpt.MinDaysFloor);
                }
                else
                {
                    if (tf == "Day")
                    {
                        requiredDays = Math.Max(requiredBars + _opt.SafetyDays, tfOpt.MinDaysFloor);
                    }
                    else if (tf == "Week")
                    {
                        var days = requiredBars * 7 + _opt.SafetyDays;
                        requiredDays = Math.Max(days, tfOpt.MinDaysFloor);
                    }
                    else // Month
                    {
                        var days = requiredBars * 31 + _opt.SafetyDays;
                        requiredDays = Math.Max(days, tfOpt.MinDaysFloor);
                    }
                }
            }


            return new WarmupPlan
            {
                TimeframeKey = tf,
                RequiredBars = requiredBars,
                RequiredDays = requiredDays
            };
        }
    }

    /// <summary>
    /// Warmup 規劃的設定檔。
    /// - Timeframes：各 timeframe 的客製規則（SMA 週期、MinBars、RequiredDays 等）
    /// - TradingMinutesPerDay：分K換算用的每日交易分鐘（預設 360；你可設更貼近市場）
    /// - PaddingBars：為了讓 SMA 等指標有緩衝，會加在 max(SMA) 後
    /// - SafetyDays：RequiredDays 估算時的額外天數緩衝
    /// </summary>
    public sealed class WarmupPlannerOptions
    {
        public Dictionary<string, TimeframeRule> Timeframes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int TradingMinutesPerDay { get; set; } = 360;  // 6 小時（自行依市場調整）
        public int PaddingBars { get; set; } = 10;            // max(SMA) 之外再多抓幾根
        public int SafetyDays { get; set; } = 2;              // 缺口日數估算安全緩衝

        public void Normalize()
        {
            // 注意：RequiredBars (== MinBars) 現在直接決定回推長度；這裡把 Week / Month 設為 60
            Ensure("Time_1", new TimeframeRule { SmaPeriods = new() { 5, 10, 20, 60 }, MinBars = 72, MinDaysFloor = 3 });
            Ensure("Time_5", new TimeframeRule { SmaPeriods = new() { 5, 10, 20, 60 }, MinBars = 72, MinDaysFloor = 10 });
            Ensure("Time_60", new TimeframeRule { SmaPeriods = new() { 5, 10, 20, 60 }, MinBars = 72, MinDaysFloor = 30 });
            Ensure("Day", new TimeframeRule { SmaPeriods = new() { 5, 10, 20, 60 }, MinBars = 160, MinDaysFloor = 260 });
            Ensure("Week", new TimeframeRule { SmaPeriods = new() { 5, 10, 20 }, MinBars = 60, MinDaysFloor = 520 });
            Ensure("Month", new TimeframeRule { SmaPeriods = new() { 5, 12, 24 }, MinBars = 60, MinDaysFloor = 3650 });
        }

        private void Ensure(string key, TimeframeRule def)
        {
            if (!Timeframes.TryGetValue(key, out var cur))
            {
                Timeframes[key] = def;
                return;
            }
            // 合併預設（避免漏填）
            cur.SmaPeriods ??= new();
            if (cur.SmaPeriods.Count == 0 && def.SmaPeriods is { Count: > 0 })
                cur.SmaPeriods.AddRange(def.SmaPeriods);
            cur.MinBars = cur.MinBars > 0 ? cur.MinBars : def.MinBars;
            cur.MinDaysFloor = cur.MinDaysFloor > 0 ? cur.MinDaysFloor : def.MinDaysFloor;
        }
    }

    public sealed class TimeframeRule
    {
        /// <summary>此 timeframe 要用到的 SMA 週期（可空；空代表不以 SMA 推導）。</summary>
        public List<int>? SmaPeriods { get; set; }

        /// <summary>最少要回傳幾根（與 SMA 推導取較大者）。</summary>
        public int MinBars { get; set; } = 0;

        /// <summary>
        /// 若有指定就使用（覆蓋估算）；否則會依分K/日/週/月估算一個保守值。
        /// </summary>
        public int? RequiredDays { get; set; }

        /// <summary>估算 RequiredDays 時的下限，避免估算過小（分K→估算/日/週/月→估算）。</summary>
        public int MinDaysFloor { get; set; } = 1;
    }
}
