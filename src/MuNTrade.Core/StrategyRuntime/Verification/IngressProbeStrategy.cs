using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System;

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    /// <summary>
    /// 用於驗證 輸入 (Ingress)/輸出 (Egress) 閘道的輕量級策略。
    /// 1. 回音 (Echo)：記錄收到的市場數據以證明輸入功能正常。
    /// 2. 探針 (Probe)：如果收到任何策略輸出事件，則拋出異常/錯誤（線路連接違規驗證）。
    /// </summary>
    public class IngressProbeStrategy : IStrategy
    {
        private readonly StrategyInstanceId _instanceId;
        private readonly StrategyTemplateId _templateId;
        private readonly ILogger _logger;
        private readonly Dictionary<Type, long> _counters = new();
        private long _totalIngressCount;

        // 訂閱有效的市場 + 系統全域 (Universe) 輸入
        // 此外，如果若需測試路由器的閘道，可以故意訂閱輸出類型（如果路由器使用明確的白名單過濾器，即使我們請求它，路由器也 *不應該* 傳送它）
        // 但對於「探針」行為，我們只訂閱所有允許的項目，並檢查我們收到了什麼。
        // 實際上，為了測試路由器的過濾功能，我們依賴路由器不傳送我們未訂閱的內容，
        // 或者路由器過濾掉我們 *確實* 訂閱了但屬於輸出的內容。
        // 在此實作中：我們訂閱有效的輸入。
        // 如果我們收到輸出事件，代表路由器未能履行其基本職責（傳送了垃圾資料）。
        private static readonly IReadOnlySet<Type> _subscriptions = new HashSet<Type>
        {
            typeof(TradeTickEvent),
            typeof(QuoteBest5Event),
            typeof(SymbolSubscribedEvent),
            typeof(SubscriptionFailedEvent),
            // 我們 *不* 訂閱 StrategyPacketEmittedEvent。
            // 如果路由器過於寬鬆，當我們使用「訂閱全部 (SubscribeAll)」邏輯（該邏輯已移除）時，它可能無論如何都會發送。
        };

        public IngressProbeStrategy(StrategyInstanceId instanceId, StrategyTemplateId templateId, ILogger logger)
        {
            _instanceId = instanceId;
            _templateId = templateId;
            _logger = logger;
            _logger.LogInformation("IngressProbeStrategy 已建構。");
        }

        public StrategyInstanceId InstanceId => _instanceId;
        public StrategyTemplateId TemplateId => _templateId;
        public string Version => "1.0.0-PROBE";

        public IReadOnlyCollection<Type> Subscriptions => _subscriptions; // IReadOnlySet 實作了 IReadOnlyCollection

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("IngressProbeStrategy 已啟動。準備探測 ingress gate。");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("IngressProbeStrategy 已停止。最終統計：{Stats}", FormatStats());
            return Task.CompletedTask;
        }

        public ValueTask OnEventAsync(IMuEvent evt, CancellationToken cancellationToken = default)
        {
            // Output events must not pass through the ingress gate.
            if (evt is StrategyPacketEmittedEvent ||
                evt is StrategyIntentEmittedEvent ||
                evt is StrategyStateChangedEvent)
            {
                _logger.LogCritical("嚴重錯誤：ingress gate 收到輸出事件：{EventType}", evt.GetType().Name);
                // 可選擇拋出異常以讓執行時崩潰並警示操作員
                throw new InvalidOperationException($"Ingress gate check failed: received output event {evt.GetType().Name}");
            }

            // 僅過濾 TX00 以驗證特定合約的源資料
            if (evt is TradeTickEvent tick && tick.Symbol != "TX00")
            {
                return ValueTask.CompletedTask;
            }

            // 2. 回音 (ECHO) 邏輯
            // 計數並記錄
            var t = evt.GetType();
            if (!_counters.ContainsKey(t)) _counters[t] = 0;
            _counters[t]++;
            _totalIngressCount++;

            if (_totalIngressCount % 10 == 0)
            {
                _logger.LogInformation("探針存活中：已收到 {Total} 個事件。最近事件：{EventType}。統計：{Stats}",
                    _totalIngressCount, t.Name, FormatStats());
            }

            return ValueTask.CompletedTask;
        }

        private string FormatStats()
        {
            return string.Join(", ", _counters);
        }
    }
}
