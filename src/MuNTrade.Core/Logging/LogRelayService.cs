using Microsoft.Extensions.Hosting;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using Serilog.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Logging
{
    /// <summary>
    /// 將 Serilog 的日誌事件轉發到 IEventBus 的託管服務 (DI 注入版本)。
    /// </summary>
    public class LogRelayService : IHostedService
    {
        private readonly IEventBus _bus;
        private readonly InMemorySink _sink; // 直接持有 Sink 的參考
        private readonly IClock _clock;
        private IDisposable? _subscription;

        /// <summary>
        /// 修改建構子，直接注入 InMemorySink。
        /// </summary>
        public LogRelayService(IEventBus bus, InMemorySink sink, IClock clock)
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] LogRelayService LogEventRaisedEvent type: {typeof(LogEventRaisedEvent).AssemblyQualifiedName}");

            _bus = bus;
            _sink = sink;
            _clock = clock;
        }

        public Task StartAsync(CancellationToken _)
        {
            // 直接訂閱注入進來的 Sink，不再需要從全域 Logger 尋找
            _subscription = _sink.Subscribe(OnLogEvent);
            return Task.CompletedTask;
        }

        private void OnLogEvent(LogEvent e)
        {
            // ▼▼▼ 在這裡加入偵錯驗證 ▼▼▼
            // 這可以 100% 確認 LogRelayService 是否真的從 Sink 收到了日誌。
            // System.Diagnostics.Debug.WriteLine($"[LogRelayService] 已收到日誌並準備發布: {e.RenderMessage()}");

            // 透過事件總線發布新的日誌事件，讓所有訂閱者 (例如 MainViewModel) 都能收到。
            _bus.Publish(new LogEventRaisedEvent
            {
                Event = e,
                OccurredAt = new DateTimeOffset(_clock.Now)
            });
        }

        public Task StopAsync(CancellationToken _)
        {
            _subscription?.Dispose();
            return Task.CompletedTask;
        }
    }
}

