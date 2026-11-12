using System;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Core.Strategies.Sandbox
{
    public class EchoStrategy : IStrategy
    {
        public StrategyInstanceId InstanceId { get; }
        public StrategyTemplateId TemplateId { get; }
        public string Version => "1.0.0-sandbox";

        private readonly IStrategyRuntimeContext _context;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public EchoStrategy(StrategyInstanceId instanceId, StrategyTemplateId templateId, IStrategyRuntimeContext context)
        {
            InstanceId = instanceId;
            TemplateId = templateId;
            _context = context;
        }

        public IReadOnlyCollection<Type> Subscriptions => new[] { typeof(MuNTrade.Abstractions.Events.System.StrategyPulseEvent) };

        public ValueTask OnEventAsync(MuNTrade.Abstractions.Contracts.Eventing.IMuEvent evt, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Emit Starting State via Router
            _context.OutputRouter.EmitStateChange(
                InstanceId,
                "ECHO",
                StrategyState.Initializing,
                StrategyState.Running,
                "Echo Strategy Started"
            );

            // No self-loop needed now, triggered by Pulse Event via Router.
            // But we keep _cts for StopAsync consistency.
            await Task.CompletedTask;
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            // Deprecated in favor of Pulse
            await Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                if (_loopTask != null)
                {
                    try { await _loopTask; } catch { }
                }

                _context.OutputRouter.EmitStateChange(
                    InstanceId,
                    "ECHO",
                    StrategyState.Running,
                    StrategyState.Stopped,
                    "Echo Strategy Stopped"
                );
            }
        }
    }
}

