
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Rules;
using MuNTrade.Core.Settings;
using System;

namespace MuNTrade.Core.Trading.Rules
{
    /// <summary>
    /// 實作「進場時間窗口」的風控規則。
    /// </summary>
    public sealed class EntryWindowRule : IRiskRule
    {
        private readonly TimeSpan _firstEntryTime;
        private readonly TimeSpan _lastEntryTime;

        public EntryWindowRule(IOptions<StrategySettings> options)
        {
            var settings = options.Value;
            _firstEntryTime = settings.FirstEntryTime;
            _lastEntryTime = settings.LastEntryTime;
        }

        /// <summary>
        /// </summary>
        /// <summary>
        /// </summary>
        public RiskDecision Evaluate(OrderRequest request, Position currentPosition)
        {
            //  智慧判斷：如果不是開新倉，則本規則不適用，直接批准
            // 這可以避免在盤末阻止停損單等平倉訊號的執行。
            if (currentPosition.Quantity != 0)
            {
                return RiskDecision.Approve();
            }

            // 如果是開新倉 (currentPosition.Quantity == 0)，才檢查時間窗口
            var signal = request.OriginalSignal;
            var signalTime = signal.SignalTime.TimeOfDay;

            if (signalTime < _firstEntryTime || signalTime > _lastEntryTime)
            {
                return RiskDecision.Reject($"不符開新倉時間窗 ({_firstEntryTime:hh\\:mm\\:ss} - {_lastEntryTime:hh\\:mm\\:ss})");
            }

            return RiskDecision.Approve();
        }
    }
}
