using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    public sealed class LedgerProbeTemplate : IStrategyTemplate
    {
        public static readonly StrategyTemplateId Id = new("Verification.10C.LedgerProbe");
        public StrategyTemplateId TemplateId => Id;
        public string Version => "1.0.0";

        public System.Collections.Generic.IReadOnlyList<WarmupRequirement> WarmupRequirements => System.Array.Empty<WarmupRequirement>();

        // Current Design: StrategyRegistry injects Services via StrategyHost if needed, 
        // but here creation logic usually resides in Registry or Template.
        // Assuming we have access to ServiceProvider or we manually resolve.
        // Checking existing templates: usually they are simple or we need the Context to carry Services.
        // StrategyRuntimeContext has no Services property in interface.
        // But StrategyRegistry has _serviceProvider.
        // Let's assume for now we need pass services via constructor of Template? 
        // Or Registry does the resolution.
        // Wait, StrategyRegistry.cs handles the creation usually. 
        // Let's implement Create with DI resolution assumption.

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

