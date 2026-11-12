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
    /// 跨日後自動刷新靜態資料（昨收/昨量/漲跌停等），並觸發 Quote 刷新以更新 UI。
    /// </summary>
    public sealed class DailyStaticDataRefreshTask : DailyTradingScheduleTask
    {
        private readonly ISubscriptionManager _subscriptionManager;
        private readonly ILogger<DailyStaticDataRefreshTask> _logger;
        private readonly TradingScheduleOptions _options;

        public DailyStaticDataRefreshTask(
            ISubscriptionManager subscriptionManager,
            IOptions<TradingScheduleOptions> options,
            ILogger<DailyStaticDataRefreshTask> logger)
        {
            _subscriptionManager = subscriptionManager;
            _logger = logger;
            _options = options?.Value ?? new TradingScheduleOptions();
        }

        public override string Name => "跨日靜態資料刷新";

        protected override TimeSpan TriggerTime => _options.StaticDataRefreshTime;

        protected override async Task RunAsync(DateTime now, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Scheduler] 觸發跨日靜態資料刷新，時間 {Time:yyyy-MM-dd HH:mm:ss}", now);

            // 刷新所有當前已訂閱清單
            await _subscriptionManager.RefreshStaticDataAsync(null, "DailyScheduler");

            // 僅刷新 Quote，ticks 不動（避免重設逐筆頁面編號）
            await _subscriptionManager.RefreshQuotesAndTicksAsync(quoteSymbols: null, tickSymbols: Array.Empty<string>());
        }
    }
}


