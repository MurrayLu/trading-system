using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Warmup
{
    public sealed class WarmupPlan
    {
        public string TimeframeKey { get; init; } = "";  // "Time_1","Time_5","Time_60","Day","Week","Month"
        public int RequiredBars { get; init; }        // 暖機後要回傳的列 K 根數（給指標起算）
        public int RequiredDays { get; init; }        // 缺口評估用的日覆蓋範圍（LCD 向前）
    }

    public interface IWarmupPlanner
    {
        IEnumerable<WarmupPlan> GetPlans(string symbol);
    }

    /// <summary>預設規劃（可換成設定檔）。</summary>
    public sealed class SimpleWarmupPlanner : IWarmupPlanner
    {
        public IEnumerable<WarmupPlan> GetPlans(string symbol)
        {
            // 可依實務調整（重點是 bars 與 days 分開定義）
            yield return new WarmupPlan { TimeframeKey = "Time_1", RequiredBars = 360, RequiredDays = 30 };
            yield return new WarmupPlan { TimeframeKey = "Time_5", RequiredBars = 360, RequiredDays = 60 };
            yield return new WarmupPlan { TimeframeKey = "Time_60", RequiredBars = 360, RequiredDays = 90 };
            yield return new WarmupPlan { TimeframeKey = "Day", RequiredBars = 360, RequiredDays = 260 };
            yield return new WarmupPlan { TimeframeKey = "Week", RequiredBars = 360, RequiredDays = 520 };
            yield return new WarmupPlan { TimeframeKey = "Month", RequiredBars = 360, RequiredDays = 3650 };
        }
    }
}

