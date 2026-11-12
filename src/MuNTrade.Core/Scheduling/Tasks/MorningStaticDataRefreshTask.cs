using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Core.Settings;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Scheduling.Tasks
{
    /// <summary>
    /// 早盤前刷新靜態資料與 Quote 訂閱，避免開盤時用到過期資料。
    /// </summary>
    public sealed class MorningStaticDataRefreshTask : DailyTradingScheduleTask
    {
        private readonly ISubscriptionManager _subscriptionManager;
        private readonly ILogger<MorningStaticDataRefreshTask> _logger;
        private readonly TradingScheduleOptions _options;

        public MorningStaticDataRefreshTask(
            ISubscriptionManager subscriptionManager,
            IOptions<TradingScheduleOptions> options,
            ILogger<MorningStaticDataRefreshTask> logger)
        {
            _subscriptionManager = subscriptionManager;
            _logger = logger;
            _options = options?.Value ?? new TradingScheduleOptions();
        }

        public override string Name => "早盤靜態資料刷新";

        protected override TimeSpan TriggerTime => _options.MorningStaticDataRefreshTime;

        protected override async Task RunAsync(DateTime now, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Scheduler] 觸發早盤靜態資料刷新：{Time:yyyy-MM-dd HH:mm:ss}", now);

            await _subscriptionManager.RefreshStaticDataAsync(null, "MorningScheduler");
            await _subscriptionManager.RefreshQuotesAndTicksAsync(quoteSymbols: null, tickSymbols: Array.Empty<string>());
        }
    }
}

