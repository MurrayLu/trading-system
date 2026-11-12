using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;

namespace MuNTrade.Abstractions.Contracts.Services
{
    /// <summary>可被 Optimizer 呼叫的回測引擎。</summary>
    public interface IBacktestEngine
    {
        /// <param name="segments">要跑的 日期 / 股票 / 區段</param>
        Task<BacktestPerformance> RunAsync(
            IEnumerable<BacktestSegment> segments,
            CancellationToken ct = default);
    }

    /// <summary>一段「某日某股在某時段」</summary>
    public record BacktestSegment(
        string Symbol,
        DateOnly  Date,
        TimeOnly  From,
        TimeOnly  To);
}

