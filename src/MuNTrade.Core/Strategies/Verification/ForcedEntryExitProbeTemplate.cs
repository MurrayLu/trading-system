using System;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.Strategies.Verification
{
    public sealed class ForcedEntryExitProbeTemplate : IStrategyTemplate
    {
        public static readonly StrategyTemplateId Id = new("Verification.Probe.ForcedEntryExit");

        public StrategyTemplateId TemplateId => Id;
        public string Version => "1.0.0";
        public IReadOnlyList<WarmupRequirement> WarmupRequirements => Array.Empty<WarmupRequirement>();

        public IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context)
        {
            return new ForcedEntryExitProbeStrategy(instanceId, TemplateId, context);
        }
    }
}

