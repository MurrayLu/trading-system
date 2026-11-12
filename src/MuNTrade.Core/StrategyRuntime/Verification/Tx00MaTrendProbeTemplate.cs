using System;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    public class Tx00MaTrendProbeTemplate : IStrategyTemplate
    {
        public static readonly StrategyTemplateId Id = new StrategyTemplateId("Tx00MaTrendProbe");

        public StrategyTemplateId TemplateId => Id;
        public string Version => "1.0.0";

        public System.Collections.Generic.IReadOnlyList<WarmupRequirement> WarmupRequirements => System.Array.Empty<WarmupRequirement>();

        public IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context)
        {
            return new Tx00MaTrendProbeStrategy(instanceId, TemplateId, context);
        }
    }
}

