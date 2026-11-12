using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Core.Settings;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Scheduling.Tasks
{
    public sealed class EnterMonitorTask : DailyTradingScheduleTask
    {
        private readonly ICapitalApiService _api;
        private readonly ILogger<EnterMonitorTask> _logger;
        private readonly TradingScheduleOptions _options;

        public EnterMonitorTask(
            ICapitalApiService api,
            IOptions<TradingScheduleOptions> options,
            ILogger<EnterMonitorTask> logger)
        {
            _api = api;
            _logger = logger;
            _options = options?.Value ?? new TradingScheduleOptions();
        }

        public override string Name => "EnterMonitor";

        protected override TimeSpan TriggerTime => _options.EnterMonitorTime;

        protected override Task RunAsync(DateTime now, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Scheduler] EnterMonitor at {Time:yyyy-MM-dd HH:mm:ss}", now);
            _api.ResumeReconnect("EnterMonitor");
            _api.EnterMonitor();
            return Task.CompletedTask;
        }
    }
}

