using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System;
namespace MuNTrade.Core.StrategyRuntime.Routing
{
    public sealed class MarketEventRouter : BackgroundService
    {
        private readonly IStrategyHost _host;
        private readonly IEventBus _eventBus;
        private readonly ILogger<MarketEventRouter> _logger;

        // Pit 1: CTS Management
        private CancellationTokenSource? _cts;

        private readonly List<IDisposable> _subscriptions = new();

        // Pit 2: Versioned Subscription Cache
        private readonly ConcurrentDictionary<string, (long Version, HashSet<Type> Set)> _subscriptionCache = new();

        // Throttling for wiring violations (log at most once every 5 seconds)
        private long _lastWiringViolationLogTicks;

        // Bounded Concurrency: Limit concurrent deliveries to prevent ThreadPool starvation with many strategies
        private readonly SemaphoreSlim _deliveryThrottler = new(16);

        // 10B-1: Ingress Whitelist moved to MarketIngressUniverse - [Removed]

        // 10B: Explicit Egress Types (Guard against wiring violations)
        private static readonly Type[] ExplicitEgressTypes = new[]
        {
            typeof(StrategyPacketEmittedEvent),
            typeof(StrategyIntentEmittedEvent),
            typeof(StrategyStateChangedEvent),
        };

        public MarketEventRouter(IStrategyHost host, IEventBus eventBus, ILogger<MarketEventRouter> logger)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            // Pit 1: Link token
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 10B-2: Router only subscribes to Whitelisted Events explicitly (No SubscribeAll)
            foreach (var type in MuNTrade.Core.Eventing.MarketIngressUniverse.All)
            {
                if (type == typeof(TradeTickEvent)) _subscriptions.Add(_eventBus.Subscribe<TradeTickEvent>(OnIngress));
                else if (type == typeof(QuoteBest5Event)) _subscriptions.Add(_eventBus.Subscribe<QuoteBest5Event>(OnIngress));
                else if (type == typeof(KLinePartialUpdatedEvent)) { } // Not subscribing yet (placeholder if needed) or just skip if no handler logic

                else if (type == typeof(SymbolSubscribedEvent)) _subscriptions.Add(_eventBus.Subscribe<SymbolSubscribedEvent>(OnIngress));
                else if (type == typeof(SymbolUnsubscribedEvent)) _subscriptions.Add(_eventBus.Subscribe<SymbolUnsubscribedEvent>(OnIngress));
                else if (type == typeof(TicksSubscribedEvent)) _subscriptions.Add(_eventBus.Subscribe<TicksSubscribedEvent>(OnIngress));
                else if (type == typeof(TicksUnsubscribedEvent)) _subscriptions.Add(_eventBus.Subscribe<TicksUnsubscribedEvent>(OnIngress));
                else if (type == typeof(SubscriptionFailedEvent)) _subscriptions.Add(_eventBus.Subscribe<SubscriptionFailedEvent>(OnIngress));

                else if (type == typeof(KLineClosedEvent)) _subscriptions.Add(_eventBus.Subscribe<KLineClosedEvent>(OnIngress));
                else if (type == typeof(IndicatorsUpdatedEvent)) _subscriptions.Add(_eventBus.Subscribe<IndicatorsUpdatedEvent>(OnIngress));

                else if (type == typeof(StrategyPulseEvent)) _subscriptions.Add(_eventBus.Subscribe<StrategyPulseEvent>(OnIngress));
            }

            _logger.LogInformation("MarketEventRouter Started. Ingress Universe = MarketIngressUniverse.All (count={Count})", MuNTrade.Core.Eventing.MarketIngressUniverse.All.Count);
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);

                // Periodic Diagnostic Log (Unconditional for Probe Verification)
                long kCount = Interlocked.Read(ref _dbgKlineClosedCount);
                long iCount = Interlocked.Read(ref _dbgIndUpdatedCount);

                _logger.LogDebug("[Router-Diag] KLineClosed={KCount} | IndicatorsUpdated={ICount} | LastInd={Last}",
                    kCount, iCount, _dbgLastIndInfo);
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            base.StopAsync(cancellationToken);
            return DisposeResources();
        }

        public override void Dispose()
        {
            DisposeResources();
            base.Dispose();
        }

        private Task DisposeResources()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
            _cts = null;

            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();
            _subscriptionCache.Clear();
            return Task.CompletedTask;
        }

        // Diagnostic Counters (user request Step 2)
        private long _dbgKlineClosedCount;
        private long _dbgIndUpdatedCount;
        private string _dbgLastIndInfo = "";

        // ========= Ingress Only Entry =========
        private void OnIngress<T>(T evt) where T : IMuEvent
        {
            // Pit 1: Use local token checking
            var ct = _cts?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;

            // Fire-and-forget logic
            _ = RouteAsync(evt, ct);
        }

        private async Task RouteAsync(IMuEvent evt, CancellationToken ct)
        {
            // Pit 1: Early exit
            if (ct.IsCancellationRequested) return;
            if (evt is null) return;

            // Diagnostic Counting [Probe]
            if (evt is KLineClosedEvent) Interlocked.Increment(ref _dbgKlineClosedCount);
            else if (evt is IndicatorsUpdatedEvent iue)
            {
                Interlocked.Increment(ref _dbgIndUpdatedCount);
                _dbgLastIndInfo = $"{iue.Symbol}|{iue.Period}";
            }

            // 10B-3: Strict wiring violation check (Outer Shell)
            if (IsExplicitEgress(evt))
            {
                LogWiringViolationThrottled(evt.GetType());
                return;
            }

            // 10B-1/10B-2: Strict Ingress Type Gate (supports envelope payload)
            if (!TryGetIngressPayloadType(evt, out var payloadType))
            {
                return;
            }

            // Pit 4: Deep Egress Check (Payload Level)
            if (IsExplicitEgressType(payloadType))
            {
                LogWiringViolationThrottled(payloadType);
                return;
            }

            if (!IsWhitelistedIngress(payloadType))
            {
                // Not in whitelist, silent drop
                return;
            }

            // Spike 1: Get RUNNING snapshot (Zero allocation hot path)
            var statuses = _host.GetRunningInstanceSnapshot();
            // Note: status from Snapshot already strictly filtered for Running state.
            if (statuses.Count == 0) return;

            // Bounded Concurrency Dispatch
            var tasks = new List<Task>(statuses.Count);

            for (int i = 0; i < statuses.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var s = statuses[i];

                // 10A/10B: Runtime Gate (Double check, though snapshot handles it mostly)
                if (s.RuntimeState != StrategyRuntimeState.Running)
                    continue;

                // 10B-5: Strategy Subscription Check (O(1) Versioned)
                if (!IsStrategySubscribed(s, payloadType))
                    continue;

                // [New] 12-2: Spec Filtering (Symbol/TF/Ind)
                if (!AcceptBySpec(s, evt))
                    continue;

                // 10B-4: Envelope Origin Check (Future Defense)
                if (evt is EventEnvelope env && env.Producer == "StrategyEgress")
                    continue;

                // Bounded Concurrency Execution
                tasks.Add(DispatchWithThrottlerAsync(s.InstanceId, evt, ct));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        private async Task DispatchWithThrottlerAsync(StrategyInstanceId instanceId, IMuEvent evt, CancellationToken ct)
        {
            await _deliveryThrottler.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _host.DeliverAsync(instanceId, evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ReportCrash handled inside Dispatch logic, but here we catch transport errors
                _logger.LogError(ex, "Dispatch error for {InstanceId}", instanceId.Value);
            }
            finally
            {
                _deliveryThrottler.Release();
            }
        }

        // ========= Helpers =========

        private static bool IsExplicitEgress(IMuEvent evt)
        {
            return IsExplicitEgressType(evt.GetType());
        }

        private static bool IsExplicitEgressType(Type t)
        {
            for (int i = 0; i < ExplicitEgressTypes.Length; i++)
            {
                if (ExplicitEgressTypes[i].IsAssignableFrom(t))
                    return true;
            }
            return false;
        }

        private static bool IsWhitelistedIngress(Type payloadType)
        {
            var all = MuNTrade.Core.Eventing.MarketIngressUniverse.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].IsAssignableFrom(payloadType))
                    return true;
            }
            return false;
        }

        // [New] 12-2: Spec Filtering Helper
        private static bool AcceptBySpec(StrategyInstanceStatus s, IMuEvent evt)
        {
            var spec = s.IngressSpec;
            if (spec == null) return true; // No filter => Accept All

            // Always allow pulses regardless of symbol filters.
            if (evt is StrategyPulseEvent)
            {
                return true;
            }

            // Extract Metadata from Event
            string? symbol = null;
            string? period = null;
            IEnumerable<string>? keys = null;

            if (evt is KLineClosedEvent k)
            {
                symbol = k.Bar?.StockId;
                period = k.Bar?.Config?.Key;
            }
            else if (evt is IndicatorsUpdatedEvent i)
            {
                symbol = i.Symbol;
                period = i.Period;
                keys = i.Values?.Keys;
            }
            // Add other types if needed (TradeTick -> Symbol)
            if (evt is TradeTickEvent t) symbol = t.Symbol;
            if (evt is SymbolSubscribedEvent ss) symbol = ss.Symbol;

            // 1. Symbol Filter
            if (spec.Symbols != null && spec.Symbols.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(symbol) || !spec.Symbols.Contains(symbol))
                    return false;
            }

            // 2. Period Filter
            if (spec.Periods != null && spec.Periods.Count > 0)
            {
                // If event has no period (e.g. Tick), do we block or pass?
                // Usually Period filter applies only to Time-based events.
                // If event IS KLine/Ind, it MUST match.
                // If event is Tick, it has no period.
                // Default Policy: If Spec has Period filter, and Event has Period, they must match.
                if (!string.IsNullOrWhiteSpace(period))
                {
                    if (!spec.Periods.Contains(period)) return false;
                }
            }

            // 3. Indicator Key Filter
            if (spec.IndicatorKeys != null && spec.IndicatorKeys.Count > 0 && keys != null)
            {
                bool any = false;
                foreach (var key in keys)
                {
                    if (spec.IndicatorKeys.Contains(key)) { any = true; break; }
                }
                if (!any) return false;
            }

            return true;
        }

        private static bool TryGetIngressPayloadType(IMuEvent evt, out Type payloadType)
        {
            payloadType = evt.GetType();

            if (evt is EventEnvelope env && env.Payload != null)
            {
                payloadType = env.Payload.GetType();
            }

            return payloadType != null;
        }

        // Pit 3: Strict Builder
        private static HashSet<Type> BuildAcceptSet(IReadOnlyCollection<Type> subs)
        {
            var set = new HashSet<Type>();
            if (subs == null) return set;

            foreach (var t in subs)
            {
                // Strict: Concrete types only
                if (t.IsAbstract || t.IsInterface)
                    continue;

                set.Add(t);
            }
            return set;
        }

        private bool IsStrategySubscribed(StrategyInstanceStatus status, Type payloadType)
        {
            // Cache Key: StrategyId
            var key = status.StrategyId ?? $"{status.TemplateId.Value}#{status.InstanceId.Value}";
            var version = status.SubscriptionsVersion;

            // Pit 2: Versioned Cache Access
            // We use AddOrUpdate to ensure version match
            var entry = _subscriptionCache.AddOrUpdate(
                key,
                _ => (version, BuildAcceptSet(status.Subscriptions)),
                (_, old) => old.Version == version ? old : (version, BuildAcceptSet(status.Subscriptions))
            );

            return entry.Set.Contains(payloadType);
        }

        private void LogWiringViolationThrottled(Type eventType)
        {
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastWiringViolationLogTicks);
            if (last != 0 && TimeSpan.FromTicks(now - last) < TimeSpan.FromSeconds(5))
                return;

            Interlocked.Exchange(ref _lastWiringViolationLogTicks, now);

            _logger.LogError(
                "WIRING VIOLATION: Egress event {EventType} reached MarketEventRouter (Ingress). " +
                "This indicates an invalid subscription or bus wiring.",
                eventType.FullName);
        }
    }
}

