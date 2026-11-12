using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Recording
{
    public class StrategyEventRecorder : BackgroundService
    {
        private readonly IEventBus _eventBus;
        private readonly IStrategyEventStore _store;
        private readonly ILogger<StrategyEventRecorder> _logger;
        private CancellationToken _ct;

        public StrategyEventRecorder(IEventBus eventBus, IStrategyEventStore store, ILogger<StrategyEventRecorder> logger)
        {
            _eventBus = eventBus;
            _store = store;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ct = stoppingToken;
            _eventBus.Subscribe<StrategyPacketEmittedEvent>(OnEvent);
            _eventBus.Subscribe<StrategyStateChangedEvent>(OnEvent);
            _eventBus.Subscribe<StrategyIntentEmittedEvent>(OnEvent);

            _logger.LogInformation("StrategyEventRecorder Started");
            return Task.CompletedTask;
        }

        private void OnEvent<T>(T evt) where T : IMuEvent
        {
            try
            {
                StrategyInstanceId? id = null;

                if (evt is StrategyPacketEmittedEvent pkt) id = pkt.InstanceId;
                else if (evt is StrategyStateChangedEvent state) id = state.InstanceId;
                else if (evt is StrategyIntentEmittedEvent intent) id = intent.InstanceId;

                if (id.HasValue)
                {
                    // Append events asynchronously.
                    // Warning: High traffic concurrency issues possible in JsonlStore (File Lock).
                    // JsonlStore AppendAsync uses File.AppendAllTextAsync which handles some concurrency but not heavy load
                    _ = _store.AppendAsync(id.Value, evt, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording strategy event");
            }
        }
    }
}
