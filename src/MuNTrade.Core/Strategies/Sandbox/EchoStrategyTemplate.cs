using System;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.Strategies.Sandbox
{
    public class EchoStrategyTemplate : IStrategyTemplate
    {
        // Use a static ID for simplicity in sandbox
        public static readonly StrategyTemplateId Id = new StrategyTemplateId("Sandbox.Echo");

        public StrategyTemplateId TemplateId => Id;
        public string Version => "1.0.0";

        public System.Collections.Generic.IReadOnlyList<WarmupRequirement> WarmupRequirements => System.Array.Empty<WarmupRequirement>();

        public IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context)
        {
            return new EchoStrategy(instanceId, TemplateId, context);
        }
    }
}
