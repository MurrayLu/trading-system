using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    public sealed class LedgerProbeTemplate : IStrategyTemplate
    {
        public static readonly StrategyTemplateId Id = new("Verification.LedgerProbe");
        public StrategyTemplateId TemplateId => Id;
        public string Version => "1.0.0";

        public System.Collections.Generic.IReadOnlyList<WarmupRequirement> WarmupRequirements => System.Array.Empty<WarmupRequirement>();

        private readonly System.IServiceProvider _services;

        public LedgerProbeTemplate(System.IServiceProvider services)
        {
            _services = services;
        }

        public IStrategy Create(StrategyInstanceId instanceId, IStrategyRuntimeContext context)
        {
            var logger = _services.GetRequiredService<ILogger<LedgerProbeStrategy>>();
            var market = _services.GetRequiredService<IMarketInfoProvider>();

            return new LedgerProbeStrategy(
                instanceId,
                TemplateId,
                context,
                logger,
                market
            );
        }
    }
}

