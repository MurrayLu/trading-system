using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    public sealed class IngressProbeTemplate : IStrategyTemplate
    {
        public StrategyTemplateId TemplateId => new StrategyTemplateId("Verification.IngressProbe");

        public string Version => "1.0.0-probe";

        public System.Collections.Generic.IReadOnlyList<WarmupRequirement> WarmupRequirements => System.Array.Empty<WarmupRequirement>();

        public IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context)
        {
            // 路徑 1：重複使用上下文記錄器 (Context Logger)（因為介面上無法取得 LoggerFactory）
            return new IngressProbeStrategy(instanceId, TemplateId, context.Logger);
        }
    }
}
