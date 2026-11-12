using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.System;

namespace MuNTrade.Core.StrategyRuntime.Testing
{
    public class StrategyPulseService : BackgroundService
    {
        private readonly IEventBus _eventBus;
        private readonly IClock _clock;
        private readonly ILogger<StrategyPulseService> _logger;
        private readonly string _sessionId = Guid.NewGuid().ToString();

        public StrategyPulseService(IEventBus eventBus, IClock clock, ILogger<StrategyPulseService> logger)
        {
            _eventBus = eventBus;
            _clock = clock;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("StrategyPulseService Started. SessionId: {SessionId}", _sessionId);
            int seq = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                seq++;

                var evt = new StrategyPulseEvent(
                    seq,
                    "Pulse",
                    new DateTimeOffset(_clock.Now),
                    _sessionId
                );

                _eventBus.Publish(evt);
            }
        }
    }
}
