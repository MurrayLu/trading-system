using System;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Contracts.Data
{
    /// <summary>
    /// 契約：定義「K 線聚合器」的職責。
    /// </summary>
    public interface IKLineBuilder
    {
        /// <summary>一根 K 線「收尾」</summary>
        event Action<DynamicBar> OnBarClosed;

        /// <summary>一根 K 線「局部更新」：每吃進 tick 就會觸發（由 Service 做節流）</summary>
        event Action<DynamicBar> OnBarPartial;

        void ProcessTick(TickData tick);

        void RegisterConfiguration(BarConfig config);

        void UnregisterConfiguration(BarConfig config);

        /// <summary>
        /// Flushes all bars that are still being aggregated across symbols and periods.
        /// Used at the end of a backtest to complete the data stream.
        /// </summary>
        void FlushOpenBars();

        /// <summary>
        /// 清空特定商品還在聚合中的 Bar。
        /// 主要用於實盤模式下，針對單一商品數據流結束時（例如每日收盤）。
        /// </summary>
        void FlushOpenBars(string stockId);

        /// <summary>
        /// Closes bars whose end time is earlier than the specified cutoff.
        /// </summary>
        /// <param name="cutoff">截止時間</param>
        void FlushBarsExpiredBefore(ExchangeTs cutoff);

        // Seeds the current bar during service startup.
        void SeedCurrentBar(string symbol, BarConfig cfg, DynamicBar snapshot);
    }
}
