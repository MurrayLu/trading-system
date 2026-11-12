using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Hosting
{
    public sealed class StrategyHost : IStrategyHost
    {
        private readonly IEventBus _eventBus;
        private readonly IClock _clock;
        private readonly ILogger<StrategyHost> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IStrategyRegistry _registry;
        private readonly MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider _marketInfo;

        private readonly ConcurrentDictionary<StrategyInstanceId, StrategyInstanceRuntime> _instances = new();
        private volatile StrategyInstanceStatus[] _runningSnapshot = Array.Empty<StrategyInstanceStatus>(); // Snapshot

        public StrategyHost(
            IEventBus eventBus,
            IClock clock,
            ILogger<StrategyHost> logger,
            ILoggerFactory loggerFactory,
            IStrategyRegistry registry,
            MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider marketInfo)
        {
            _eventBus = eventBus;
            _clock = clock;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _registry = registry;
            _marketInfo = marketInfo;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("StrategyHost Started");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("StrategyHost Stopping...");
            foreach (var instance in _instances.Values)
            {
                await instance.StopAsync(cancellationToken);
            }
            RebuildSnapshot();
            _instances.Clear();
        }

        public IEnumerable<IStrategy> GetActiveInstances()
        {
            return _instances.Values.Select(x => x.Strategy).ToArray();
        }

        public IEnumerable<StrategyInstanceStatus> GetInstanceStatuses()
        {
            return _instances.Values
                .Select(runtime => new StrategyInstanceStatus(
                    runtime.InstanceId,
                    runtime.Strategy.TemplateId,
                    MuNTrade.Core.StrategyRuntime.StrategyIdentity.BuildStrategyId(runtime.Strategy.TemplateId.Value, runtime.InstanceId.Value),
                    runtime.AccountKey, // Pinned Key (from Runtime directly)
                    StrategyState.Running,
                    runtime.State,
                    runtime.Strategy.Subscriptions,
                    runtime.Strategy as Abstractions.Contracts.Strategy.IStrategyIngressSpec,
                    runtime.RunGeneration, // Version
                    runtime.LastSeenAt))
                .ToArray();
        }

        public IReadOnlyList<StrategyInstanceStatus> GetRunningInstanceSnapshot()
        {
            return _runningSnapshot;
        }

        private void RebuildSnapshot()
        {
            // Filter only RUNNING instances for the hot-path snapshot
            _runningSnapshot = _instances.Values
                .Where(r => r.State == MuNTrade.Abstractions.Events.Strategy.StrategyRuntimeState.Running)
                .Select(runtime => new StrategyInstanceStatus(
                    runtime.InstanceId,
                    runtime.Strategy.TemplateId,
                    MuNTrade.Core.StrategyRuntime.StrategyIdentity.BuildStrategyId(runtime.Strategy.TemplateId.Value, runtime.InstanceId.Value),
                    runtime.AccountKey, // Pinned Key (from Runtime directly)
                    StrategyState.Running,
                    runtime.State,
                    runtime.Strategy.Subscriptions,
                    runtime.Strategy as Abstractions.Contracts.Strategy.IStrategyIngressSpec,
                    runtime.RunGeneration,
                    runtime.LastSeenAt)) // LastSeenAt in snapshot might appear slightly stale but acceptable for routing config
                .ToArray();
        }

        public async ValueTask DeliverAsync(StrategyInstanceId instanceId, IMuEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt == null)
            {
                return;
            }

            if (_instances.TryGetValue(instanceId, out var runtime))
            {
                await runtime.DeliverAsync(evt, cancellationToken);
            }
            else
            {
                _logger.LogDebug("DeliverAsync: Instance {InstanceId} not found", instanceId.Value);
            }
        }

        public void ReportStrategyCrash(StrategyInstanceId instanceId, Exception ex)
        {
            if (_instances.TryGetValue(instanceId, out var runtime))
            {
                runtime.MarkFaulted();
                RebuildSnapshot(); // Remove from Routing Snapshot

                _eventBus.Publish(new StrategyStateChangedEvent(
                   new MuNTrade.Abstractions.Time.ArrivalTs(_clock.Now),
                   instanceId,
                   "Unknown",
                   StrategyState.Running,
                   StrategyState.Faulted,
                   ex.Message
               )
                {
                    OccurredAt = new DateTimeOffset(_clock.Now)
                });

                // Also emit packet
                var pkt = new StrategyPacketEmittedEvent(
                    runtime.Strategy.TemplateId,
                    instanceId,
                    StrategyScope.System,
                    null,
                    StrategyCategory.State,
                    StrategySeverity.Error,
                    ActionCode: "Crash",
                    Message: ex.ToString(),
                    OccurredAt: new DateTimeOffset(_clock.Now)
                );
                _eventBus.Publish(pkt);

                _logger.LogError(ex, "Strategy Instance {InstanceId} Crashed", instanceId.Value);
            }
        }

        public async Task<StrategyInstanceId> StartInstanceAsync(StrategyRecipeId recipeId, string accountKey, CancellationToken cancellationToken = default)
        {
            // The recipe ID is used as the template ID.
            var templateIdString = recipeId.Value;

            var template = _registry.Templates.FirstOrDefault(t => t.TemplateId.Value == templateIdString);
            if (template == null)
            {
                // Fallback: try to find by suffix if full name not found (just in case)
                template = _registry.Templates.FirstOrDefault(t => t.TemplateId.Value.EndsWith(templateIdString));
                if (template == null)
                    throw new ArgumentException($"Template not found for recipe/template ID: {templateIdString}");
            }

            var instanceId = new StrategyInstanceId(Guid.NewGuid().ToString());

            // Create Context
            var strategyLogger = _loggerFactory.CreateLogger($"Strategy.{template.TemplateId.Value}.{instanceId.Value}");

            // Inject market information and the runtime account key into the strategy context.
            var outputRouter = new Routing.StrategyOutputRouter(_eventBus, _clock, _marketInfo, accountKey);
            var context = new StrategyRuntimeContext(_eventBus, _clock, strategyLogger, outputRouter, accountKey);

            var strategy = template.Create(instanceId, context);

            // Pass accountKey to Runtime as well for Status exposure
            var runtime = new StrategyInstanceRuntime(strategy, _clock, strategyLogger, accountKey);

            // Configure Router State Accessor for Gating using STRICT Runtime State
            outputRouter.ConfigureStateAccessor(() => runtime.State);

            await runtime.StartAsync(cancellationToken);

            if (!_instances.TryAdd(instanceId, runtime))
            {
                await runtime.StopAsync(cancellationToken);
                throw new InvalidOperationException($"Instance ID collision: {instanceId.Value}");
            }

            RebuildSnapshot(); // Add to Routing Snapshot

            _logger.LogInformation("Started strategy instance {InstanceId} (Template: {TemplateId}, Account: {AccountKey})", instanceId.Value, template.TemplateId.Value, accountKey);

            return instanceId;
        }

        public async Task StopInstanceAsync(StrategyInstanceId instanceId, CancellationToken cancellationToken = default)
        {
            if (_instances.TryGetValue(instanceId, out var runtime))
            {
                await runtime.StopAsync(cancellationToken);

                // Remove after stop so UI doesn't accumulate stale rows.
                _instances.TryRemove(instanceId, out _);
                RebuildSnapshot(); // Remove from Routing Snapshot

                _logger.LogInformation("Stopped and removed strategy instance {InstanceId}", instanceId.Value);
            }
            else
            {
                _logger.LogWarning("StopInstanceAsync: Instance {InstanceId} not found", instanceId.Value);
            }
        }
    }
}

