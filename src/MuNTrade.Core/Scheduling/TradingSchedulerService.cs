using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Scheduling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Scheduling
{
    /// <summary>
    /// 輕量化排程服務：集中管理所有 ITradingScheduleTask，並使用 IClock 時間來源。
    /// </summary>
    public sealed class TradingSchedulerService : BackgroundService
    {
        private readonly IReadOnlyList<ITradingScheduleTask> _tasks;
        private readonly IClock _clock;
        private readonly ILogger<TradingSchedulerService> _logger;
        private readonly TimeSpan _idleDelay;
        private readonly Dictionary<ITradingScheduleTask, DateTime> _nextCheck = new();

        public TradingSchedulerService(
            IEnumerable<ITradingScheduleTask> tasks,
            IClock clock,
            ILogger<TradingSchedulerService> logger)
        {
            _tasks = tasks?.ToList() ?? new List<ITradingScheduleTask>();
            _clock = clock;
            _logger = logger;
            _idleDelay = TimeSpan.FromSeconds(5);

            foreach (var task in _tasks)
            {
                _nextCheck[task] = DateTime.MinValue;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_tasks.Count == 0)
            {
                _logger.LogInformation("[Scheduler] 目前沒有註冊任何排程，服務保持待命狀態。");
                await Task.Delay(Timeout.Infinite, stoppingToken);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = _clock.Now;

                foreach (var task in _tasks)
                {
                    if (now < _nextCheck[task])
                        continue;

                    _nextCheck[task] = now + task.PollingInterval;

                    if (!task.ShouldRun(now))
                        continue;

                    try
                    {
                        _logger.LogInformation("[Scheduler] 執行排程：{Task}，時間 {Time:HH:mm:ss}", task.Name, now);
                        await task.ExecuteAsync(now, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Scheduler] 排程 {Task} 執行失敗", task.Name);
                    }
                }

                await Task.Delay(_idleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

