using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public sealed record WarmupRequirement(TimeSpan TimeFrame, int LookbackRows);

    public interface IStrategyTemplate
    {
        StrategyTemplateId TemplateId { get; }
        string Version { get; }

        /// <summary>
        /// 宣告策略在實體啟動前，需要的歷史行情資料 (Warmup)
        /// </summary>
        IReadOnlyList<WarmupRequirement> WarmupRequirements { get; }

        IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context);
    }
}

