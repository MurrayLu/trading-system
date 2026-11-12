using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Core.Settings;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Scheduling.Tasks
{
    public sealed class LeaveMonitorTask : DailyTradingScheduleTask
    {
        private readonly ICapitalApiService _api;
        private readonly ILogger<LeaveMonitorTask> _logger;
        private readonly TradingScheduleOptions _options;

        public LeaveMonitorTask(
            ICapitalApiService api,
            IOptions<TradingScheduleOptions> options,
            ILogger<LeaveMonitorTask> logger)
        {
            _api = api;
            _logger = logger;
            _options = options?.Value ?? new TradingScheduleOptions();
        }

        public override string Name => "LeaveMonitor";

        protected override TimeSpan TriggerTime => _options.LeaveMonitorTime;

        protected override Task RunAsync(DateTime now, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Scheduler] LeaveMonitor at {Time:yyyy-MM-dd HH:mm:ss}", now);
            var duration = _options.EnterMonitorTime - _options.LeaveMonitorTime;
            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromMinutes(90);
            _api.SuspendReconnect(duration, "LeaveMonitor");
            _api.LeaveMonitor();
            return Task.CompletedTask;
        }
    }
}

