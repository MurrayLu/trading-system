using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MuNTrade.Core.DataProcessing
{
    public class DataAggregatorService : BackgroundService
    {
        private readonly ILogger<DataAggregatorService> _logger;
        private readonly ChannelReader<TickData> _tickChannelReader;
        private readonly ChannelWriter<DynamicBar> _barChannelWriter;
        private readonly DataAggregatorSettings _settings;
        private readonly Dictionary<string, AggregatorState> _aggregatorStates;

        private class AggregatorState
        {
            public BarConfig Config { get; }
            public Dictionary<string, DynamicBar> CurrentBars { get; } = new();
            public AggregatorState(BarConfig config) { Config = config; }
        }

        public DataAggregatorService(
            ILogger<DataAggregatorService> logger,
            ChannelReader<TickData> tickChannelReader,
            ChannelWriter<DynamicBar> barChannelWriter,
            IOptions<DataAggregatorSettings> settings)
        {
            _logger = logger;
            _tickChannelReader = tickChannelReader;
            _barChannelWriter = barChannelWriter;
            _settings = settings.Value;

            _aggregatorStates = new Dictionary<string, AggregatorState>();
            if (_settings.EnabledBarConfigs != null)
            {
                foreach (var config in _settings.EnabledBarConfigs)
                {
                    string key = $"{config.Type}_{config.Period}";
                    _aggregatorStates[key] = new AggregatorState(config);
                    _logger.LogInformation("[D1] 已啟用 Bar 生產線: {Key}", key);
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[D1] 資料聚合器已啟動 (多週期模式)。");
            try
            {
                await foreach (var tick in _tickChannelReader.ReadAllAsync(stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    foreach (var state in _aggregatorStates.Values)
                    {
                        ProcessTickForConfig(tick, state);
                    }
                }
            }
            finally
            {
                // 1️⃣ 把還沒收尾的 Bar 全部補發一次
                foreach (var state in _aggregatorStates.Values)
                {
                    foreach (var currentBar in state.CurrentBars.Values)
                    {
                        if (currentBar.TickCount > 0) // 確保有 Tick 資料
                        {
                            PublishBar(currentBar);
                        }
                    }
                }
                _barChannelWriter.TryComplete();
                _logger.LogInformation("[D1] 聚合器完成並關閉 Bar Channel。");
            }
        }

        private void ProcessTickForConfig(TickData tick, AggregatorState state)
        {
            // 依 config 決定是否時間棒或 tick 棒（保留原邏輯）
            if (!state.CurrentBars.TryGetValue(tick.StockId, out var currentBar))
            {
                currentBar = CreateFirstBar(state.Config, tick);
                state.CurrentBars[tick.StockId] = currentBar;
            }
            else
            {
                currentBar.AddTick(tick);
            }

            bool isBarClosed = false;

            if (state.Config.Type.Equals("Time", StringComparison.OrdinalIgnoreCase))
            {
                // 直接對照「這根的固定 EndTime」
                if (tick.ExchangeTs >= currentBar.EndTime) isBarClosed = true;
            }
            else // Tick-bar aggregation.
            {
                if (currentBar.TickCount >= state.Config.Period) isBarClosed = true;
            }

            if (isBarClosed)
            {
                PublishBar(currentBar);
                state.CurrentBars.Remove(tick.StockId);

                // 用這筆 tick 開下一根（注意：不要動上一根的 EndTime）
                var nextBar = CreateNextBar(state.Config, tick);
                state.CurrentBars[tick.StockId] = nextBar;
            }
        }

        //小工具：時間對齊 ----------------

        private static DateTime TruncateToMinute(DateTime dt)
            => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);

        /// <summary>
        /// 傳回「從 startTime 起算，下一個 period 邊界」；等價既有的 GetTimeBarEndTime。
        /// </summary>
        private static DateTime GetTimeBarEndTime(DateTime startTime, int timeFrameMinutes)
        {
            long periodTicks = TimeSpan.FromMinutes(timeFrameMinutes).Ticks;
            long aligned = (startTime.Ticks / periodTicks) * periodTicks; // 去頭對齊
            return new DateTime(aligned + periodTicks, startTime.Kind);
        }

        //建 Bar：改用 FromSnapshot（避免寫入 private setter） ----------------

        private DynamicBar CreateFirstBar(BarConfig cfg, TickData tick)
        {
            // 這裡採「當分鐘起算」的簡化：不使用 anchor，只用 floor 到分鐘
            // 若要和 KLineBuilder 的 anchor/AlignUp 完全一致，可把這個服務改成呼叫 KLineBuilder 來產生。
            if (cfg.Type.Equals("Time", StringComparison.OrdinalIgnoreCase))
            {
                var start = TruncateToMinute(tick.ExchangeTs.Value);
                var end = GetTimeBarEndTime(start, cfg.Period);
                var p = tick.Price;
                return DynamicBar.FromSnapshot(
                    tick.StockId, cfg,
                    new ExchangeTs(start), new ExchangeTs(end),
                    p, p, p, p,
                    tick.Volume, 1, tick.ExchangeTs
                );
            }
            else
            {
                // Tick 型：以第一筆 tick 時間作為起始；EndTime 暫時用 start+period「分」只是佔位
                // 若 Tick 型的 period 是「幾筆」，EndTime 其實不重要；只在關棒才輸出。
                var start = tick.ExchangeTs.Value;
                var end = start.AddMinutes(cfg.Period);
                var p = tick.Price;
                return DynamicBar.FromSnapshot(
                    tick.StockId, cfg,
                    new ExchangeTs(start), new ExchangeTs(end),
                    p, p, p, p,
                    tick.Volume, 1, tick.ExchangeTs
                );
            }
        }

        private DynamicBar CreateNextBar(BarConfig cfg, TickData tick)
        {
            // 以「這筆 tick 所在分鐘」重新起一根
            if (cfg.Type.Equals("Time", StringComparison.OrdinalIgnoreCase))
            {
                var start = TruncateToMinute(tick.ExchangeTs.Value);
                var end = GetTimeBarEndTime(start, cfg.Period);
                var p = tick.Price;
                return DynamicBar.FromSnapshot(
                    tick.StockId, cfg,
                    new ExchangeTs(start), new ExchangeTs(end),
                    p, p, p, p,
                    tick.Volume, 1, tick.ExchangeTs
                );
            }
            else
            {
                var start = tick.ExchangeTs.Value;
                var end = start.AddMinutes(cfg.Period);
                var p = tick.Price;
                return DynamicBar.FromSnapshot(
                    tick.StockId, cfg,
                    new ExchangeTs(start), new ExchangeTs(end),
                    p, p, p, p,
                    tick.Volume, 1, tick.ExchangeTs
                );
            }
        }

        private void PublishBar(DynamicBar bar)
        {
            _logger.LogWarning("[D1] Bar Closed & Published: Stock={StockId}, Type={BarType}, Period={Period}, CloseTime={EndTime:HH:mm:ss}",
                bar.StockId, bar.Config?.Type, bar.Config?.Period, bar.EndTime);

            if (!_barChannelWriter.TryWrite(bar))
            {
                _logger.LogDebug("[D1] BarChannel 已關閉或已滿，忽略這根 Bar。");
            }
        }
    }
}

