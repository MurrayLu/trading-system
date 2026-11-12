// MuNTrade.Core/Services/SessionFilterService.cs (雙模式最終版)

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;    // ★ 引用 IEventBus
using MuNTrade.Abstractions.Domain.MarketData.Channels;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Core.Services
{
    // ★ 修改：繼承自 BackgroundService 以支援 LIVE 模式自動啟動
    public class SessionFilterService : BackgroundService
    {
        private readonly ILogger<SessionFilterService> _logger;
        private readonly ChannelReader<TickData> _rawTickReader;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IEventBus _eventBus; // ★ 核心修改：直接注入 IEventBus
        private readonly IClock _clock;

        public SessionFilterService(
            ILogger<SessionFilterService> logger,
            RawTickChannel rawTickChannel,
            ITradingSessionProvider sessionProvider,
            IEventBus eventBus,
            IClock clock) // ★ 移除 IServiceProvider，改為 IEventBus
        {
            _logger = logger;
            _rawTickReader = rawTickChannel.Reader;
            _sessionProvider = sessionProvider;
            _eventBus = eventBus;
            _clock = clock;
        }

        /// <summary>
        /// ★ 保留：這個公開方法是給 BacktestEngine 手動呼叫的，用於同步處理單筆 Tick
        /// </summary>
        public TickData? ProcessTick(TickData tick)
        {
            var session = _sessionProvider.GetSession(tick.StockId, tick.ExchangeTs.Value);
            if (session == null || !session.IsDuringMarketHours(tick.ExchangeTs.Value))
                return null;

            // 收盤「最後一秒」的 Tick → 打旗
            if (tick.ExchangeTs.Value == session.EndTime)
                tick.IsSessionEnd = true;

            // ① 計算收盤靜止撮合的時間段
            if (session.PreCloseAuctionStart != TimeSpan.Zero)
            {
                var pcStart = tick.ExchangeTs.Value.Date + session.PreCloseAuctionStart;          // 13:25:00
                var pcEnd = pcStart + session.PreCloseExtension;                         // 13:30:00

                bool inPreCloseRange = tick.ExchangeTs.Value >= pcStart && tick.ExchangeTs.Value < pcEnd;
                bool isSimulatedFlag = tick.IsSimulated;

                if (inPreCloseRange || isSimulatedFlag)
                {
                    // 允許 13:25:00 那筆穿過，但 >= 13:25:01 就丟
                    if (tick.ExchangeTs.Value > pcStart) return null;
                }
            }

            return tick;   // 其他時間 or 13:25:00 / 13:30:00 都放行
        }


        /// <summary>
        /// ★ 保留：提供給 Backtest/UI 模式手動呼叫的公開方法
        /// </summary>
        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);

        /// <summary>
        /// ★ 新增：這是 BackgroundService 的核心執行迴圈，供 LIVE 模式自動啟動
        /// </summary>
        // ExecuteAsync 是唯一需要 Hub 的地方，所以我們在這裡解析它
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[SessionFilter] 交易時段過濾服務已啟動...");

            try
            {
                await foreach (var tick in _rawTickReader.ReadAllAsync(stoppingToken))
                {
                    var filteredTick = ProcessTick(tick);
                    if (filteredTick != null)
                    {
                        // ★ 核心修改：將過濾後的 TickData 轉換為 TradeTickEvent 並發布
                        var tradeEvent = new TradeTickEvent
                        {
                            Symbol = filteredTick.StockId,
                            FeedKind = filteredTick.FeedKind,
                            ExchangeTs = filteredTick.ExchangeTs,
                            ArrivalTs = filteredTick.ArrivalTs,
                            OccurredAt = new DateTimeOffset(_clock.Now),
                            Price = filteredTick.Price,
                            Volume = (int)Math.Clamp(filteredTick.Volume, 0, int.MaxValue),
                            IsSimulated = filteredTick.IsSimulated,
                            MarketNo = filteredTick.MarketNo,
                            Source = filteredTick.Source
                            // 注意：這裡沒有完整的 Quote 和 Best5 資訊，這是正常的，
                            // 因為那些資訊是從 CapitalApiService 的其他事件直接發布的。
                        };

                        _eventBus.Publish(tradeEvent);

                        _logger.LogDebug("[PASS] {Sym} {Ts:HH:mm:ss.fff}",
                                         tick.StockId, tick.ExchangeTs.Value);
                    }
                    else
                    {
                        _logger.LogDebug("[DROP] {Sym} {Ts:HH:mm:ss.fff}",
                                         tick.StockId, tick.ExchangeTs.Value);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[SessionFilter] 服務已被取消。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SessionFilter] 服務執行時發生未預期錯誤。");
            }
            finally
            {
                _logger.LogInformation("[SessionFilter] 過濾服務已安全關閉。");
            }
        }
    }
}


