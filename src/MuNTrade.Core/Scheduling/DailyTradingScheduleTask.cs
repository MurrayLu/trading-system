using System;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Scheduling;

namespace MuNTrade.Core.Scheduling
{
    /// <summary>
    /// 每日定時任務的簡易抽象類別：一天只會執行一次，避免跨日重複觸發。
    /// </summary>
    public abstract class DailyTradingScheduleTask : ITradingScheduleTask
    {
        private DateTime _lastRunDate = DateTime.MinValue;

        public abstract string Name { get; }

        /// <summary>
        /// 預設 30 秒檢查一次，可依需求覆寫。
        /// </summary>
        public virtual TimeSpan PollingInterval => TimeSpan.FromSeconds(30);

        /// <summary>
        /// 目標觸發時間（local time）。
        /// </summary>
        protected abstract TimeSpan TriggerTime { get; }

        public bool ShouldRun(DateTime now)
        {
            if (_lastRunDate == now.Date)
                return false;

            var nowTime = now.TimeOfDay;
            if (nowTime.Hours == TriggerTime.Hours && nowTime.Minutes == TriggerTime.Minutes)
            {
                _lastRunDate = now.Date;
                return true;
            }

            return false;
        }

        public Task ExecuteAsync(DateTime now, CancellationToken cancellationToken)
            => RunAsync(now, cancellationToken);

        /// <summary>
        /// 讓子類別填寫實際執行內容。
        /// </summary>
        protected abstract Task RunAsync(DateTime now, CancellationToken cancellationToken);
    }
}

