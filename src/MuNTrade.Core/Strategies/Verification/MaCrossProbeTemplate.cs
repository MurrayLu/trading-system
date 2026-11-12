using System;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.Strategies.Verification
{
    public sealed class MaCrossProbeTemplate : IStrategyTemplate
    {
        public static readonly StrategyTemplateId Id = new("Verification.Probe.MaCross");

        public StrategyTemplateId TemplateId => Id;
        public string Version => "1.0.0";

        public IReadOnlyList<WarmupRequirement> WarmupRequirements { get; } = new[]
        {
            new WarmupRequirement(TimeSpan.FromMinutes(1), 120)
        };

        public IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context)
        {
            return new MaCrossProbeStrategy(instanceId, TemplateId, context);
        }
    }
}

