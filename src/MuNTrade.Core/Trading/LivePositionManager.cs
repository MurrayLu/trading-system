using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
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
using MuNTrade.Abstractions.Contracts.Trading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MuNTrade.Core.Services;
using MuNTrade.Core.Services.Market;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;

namespace MuNTrade.Core.Trading
{
    public class LivePositionManager : IPositionManagerService, IPositionReader, IDisposable
    {
        private readonly ConcurrentDictionary<(string Account, string Symbol), Position> _positions = new();
        private readonly IEventBus _bus;
        private readonly ILogger<LivePositionManager> _logger;

        // ★ 關鍵依賴：價格跳動服務 & 交易規則提供者
        private readonly IPriceTierService _priceTierService;
        private readonly ITradeRulesProvider _tradeRulesProvider;
        private readonly System.Timers.Timer _broadcastTimer;

        // ★ Persistence
        private readonly PositionSnapshotStore _snapshotStore;
        private readonly System.Timers.Timer _snapshotTimer;
        private volatile bool _snapshotDirty;
        private readonly HashSet<string> _snapshotDirtyAccounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _snapshotLock = new();
        private readonly IDisposable _brokerSnapshotToken;
        private readonly IDisposable _brokerMetricsToken;
        private readonly MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider _marketInfoProvider;

        // Patch A: 釘死目前運行模式
        private bool _isSimMode; // Un-readonly for Hot-Swap
        private DateTime _lastWarningTime = DateTime.MinValue;


        public LivePositionManager(
            IEventBus bus,
            ILogger<LivePositionManager> logger,
            IPriceTierService priceTierService,
            ITradeRulesProvider tradeRulesProvider,
            PositionSnapshotStore snapshotStore,
            MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider marketInfoProvider,
            IOptions<ApplicationSettings> appSettings)
        {
            _bus = bus;
            _logger = logger;
            _priceTierService = priceTierService;
            _tradeRulesProvider = tradeRulesProvider;
            _snapshotStore = snapshotStore;
            _marketInfoProvider = marketInfoProvider;
            _marketInfoProvider = marketInfoProvider;

            // 1. 判斷模式 (Use TradingGateway string)
            var gateway = appSettings.Value.TradingGateway?.ToUpperInvariant() ?? "PAPER";
            _isSimMode = gateway.Contains("PAPER") || gateway.Contains("SIM");
            _logger.LogInformation("[LivePositionManager] Started. Mode={Mode} (Gateway={Gateway})",
                _isSimMode ? "SIM" : "LIVE", gateway);

            _bus.Subscribe<QuoteSnapshotEvent>(OnQuoteSnapshot); // [Patch A] Drive by Quote
            _bus.Subscribe<OrderFillEvent>(UpdatePositionOnFill); // [Patch] Handle Live Fills for real-time update
            _brokerSnapshotToken = _bus.Subscribe<BrokerPositionSnapshotEvent>(OnBrokerSnapshot);
            _brokerMetricsToken = _bus.Subscribe<BrokerPositionMetricsEvent>(OnBrokerPositionMetrics);
            // [Route B] Listen to Mode Change
            _bus.Subscribe<GatewayModeChangedEvent>(OnGatewayModeChanged);


            _tradeRulesProvider.Changed += OnRulesChanged;

            RestoreSnapshots();
            RecalculateAllPositions();
            BroadcastSnapshot();

            _broadcastTimer = new System.Timers.Timer(500);
            _broadcastTimer.Elapsed += (s, e) => BroadcastSnapshot();
            _broadcastTimer.Start();

            _snapshotTimer = new System.Timers.Timer(1000)
            {
                AutoReset = false
            };
            _snapshotTimer.Elapsed += (s, e) => FlushSnapshots();
        }

        private void OnGatewayModeChanged(GatewayModeChangedEvent e)
        {
            _logger.LogInformation("[LivePositionManager] Mode Switch Requested: {NewMode}", e.Mode);

            // 1. Save current state
            FlushSnapshots();

            // 2. Clear memory (Avoid cross contamination)
            _positions.Clear();

            // 3. Update Mode
            var gateway = e.Mode.ToUpperInvariant();
            _isSimMode = gateway.Contains("PAPER") || gateway.Contains("SIM");

            // 4. Load new state
            RestoreSnapshots();
            RecalculateAllPositions();
            BroadcastSnapshot();
            _logger.LogInformation("[LivePositionManager] Mode Setup Complete. Now in {Mode}", _isSimMode ? "SIM" : "LIVE");
        }

        private void OnRulesChanged(MuNTrade.Abstractions.Domain.Capital.TradeRulesConfig config)
        {
            // 規則變了，重新計算所有持倉的 PnL/BEP/Cost
            RecalculateAllPositions();
            BroadcastSnapshot();
        }

        public void ApplyBrokerSnapshot(string accountKey, IReadOnlyList<PositionDto> positions, bool isFinal = false, MarketKind? scope = null, string? correlationId = null, string? brokerAccountNo = null)
        {
            // Patch B: 嚴格檢查 AccountKey
            if (string.IsNullOrWhiteSpace(accountKey))
            {
                _logger.LogWarning("[BrokerSnapshot] REJECTED: Missing account key {SyncId}.", correlationId);
                return;
            }

            // 確保只處理當前模式允許的帳戶
            if (_isSimMode && accountKey.Equals("Real", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[BrokerSnapshot] REJECTED: Received 'Real' snapshot in SIM mode.");
                return;
            }

            var incoming = positions
                .Where(p => !string.IsNullOrWhiteSpace(p.Symbol))
                .ToList();

            var incomingSet = new HashSet<string>(incoming.Select(p => p.Symbol), StringComparer.OrdinalIgnoreCase);

            // [Patch 5] Scoped Pruning Logic (Only if Final)
            // Use AccountKey for Dictionary Pruning
            if (isFinal)
            {
                foreach (var k in _positions.Keys.ToArray())
                {
                    if (!string.Equals(k.Account, accountKey, StringComparison.OrdinalIgnoreCase)) continue;

                    // If scope is provided, only prune symbols relevant to that scope
                    if (scope.HasValue)
                    {
                        var pos = _positions[k];
                        if (!BelongsToScope(k.Symbol, pos.MarketNo, scope.Value))
                            continue;
                    }

                    if (!incomingSet.Contains(k.Symbol))
                        _positions.TryRemove(k, out _);
                }
            }

            // Infer MarketNo for new positions
            MarketNo inferredMarket = MarketNo.Unknown;
            if (scope == MarketKind.Securities) inferredMarket = MarketNo.Listing;
            else if (scope == MarketKind.Derivatives) inferredMarket = MarketNo.Futures;

            foreach (var dto in incoming)
            {
                long signedQty = dto.Side == OrderSide.Sell ? -dto.Quantity : dto.Quantity;

                // Get Existing using AccountKey
                var existing = GetPositionInternal(accountKey, dto.Symbol);
                var avgCost = dto.AverageCost > 0 ? dto.AverageCost : existing.AveragePrice;

                _positions[(accountKey, dto.Symbol)] = new Position
                {
                    StockId = dto.Symbol,
                    Account = accountKey,     // UI Binding ? Use Key
                    AccountKey = accountKey,  // Explicit Key
                    Quantity = signedQty,
                    AveragePrice = avgCost,
                    LastPrice = dto.LastPrice,
                    MarketNo = inferredMarket // [Patch 5]
                };
            }

            RecalculateAllPositions();

            // Masking for Log
            string maskedRef = "MASKED";
            if (!string.IsNullOrEmpty(brokerAccountNo) && brokerAccountNo.Length > 4)
                maskedRef = "*" + brokerAccountNo.Substring(brokerAccountNo.Length - 4);
            else maskedRef = brokerAccountNo ?? "N/A";

            _logger.LogInformation("[LPM] Snapshot applied. Key={Key} (Acc={Masked}) Final={Final} Scope={Scope} Incoming={IncomingCount} TotalPositionsNow={TotalCount} (SyncId={SyncId})",
                accountKey, maskedRef, isFinal, scope, incoming.Count, _positions.Count, correlationId);

            BroadcastSnapshot(allowEmpty: true);

            // [Patch] Persist IMMEDIATELY if Final (truth). Ensures "zero positions" is written to file.
            if (isFinal)
            {
                var currentAccountPositions = _positions.Values
                    .Where(p => string.Equals(p.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _snapshotStore.SavePositions(accountKey, currentAccountPositions);
            }

            MarkSnapshotDirty(accountKey);
        }

        private bool BelongsToScope(string symbol, MarketNo marketNo, MarketKind scope)
        {
            // Priority 1: Use Provider to get Truth
            if (_marketInfoProvider.TryGetMarketInfo(symbol, out var info) && info.Kind != MarketKind.Unknown)
            {
                if (scope == MarketKind.Securities) return info.Kind == MarketKind.Securities;
                if (scope == MarketKind.Derivatives) return info.Kind == MarketKind.Derivatives;
            }

            // Priority 2: Use passed MarketNo (from Position object) if valid
            if (marketNo != MarketNo.Unknown)
            {
                if (scope == MarketKind.Securities)
                {
                    return marketNo == MarketNo.Listing || marketNo == MarketNo.OTC ||
                           marketNo == MarketNo.Emerging || marketNo == MarketNo.IntradayOddListing;
                }
                if (scope == MarketKind.Derivatives)
                {
                    return marketNo == MarketNo.Futures || marketNo == MarketNo.Options;
                }
            }

            // Priority 3: Fallback (Ambiguous -> Do Not Prune)
            // If we are unsure, we must NOT prune it to avoid accidental data loss.
            // exception: if IsLikelyStock is very convincing for Securities scope?
            // "寧可殘留也不要誤殺" -> Return false.
            return false;
        }

        private bool IsLikelyStock(string s) => s.Length >= 4 && s.Length <= 6 && System.Linq.Enumerable.All(s, char.IsDigit); // Helper kept but unused in strict mode

        private void RestoreSnapshots()
        {
            // [Paranoid Mode] Restore snapshots STRICTLY based on current mode
            var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (_isSimMode)
                {
                    // Sim Mode: Use "Sim:Paper:All:Default" + any SimAccounts
                    var sim = MuNTrade.Core.Capital.CapitalConfigService.LoadAllSimAccounts();
                    foreach (var a in sim) accountKeys.Add(a.Id);
                    accountKeys.Add("Sim:Paper:All:Default"); // New V2 Paper Key
                    // accountKeys.Add("Paper"); // Old compact key? Let's deprecate or map.
                    // Keep "Paper" if users have old data?
                }
                else
                {
                    // Live Mode: "Real"
                    // var live = MuNTrade.Core.Capital.CapitalConfigService.LoadAllLiveAccounts(); 
                    // Live account json files are usually Account Profiles, IDs are user defined.
                    // But effectively we force "Real" for the main one.
                    accountKeys.Add("Real");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LivePositionManager] Failed to load account list from config.");
                accountKeys.Add(ResolveDefaultAccount());
            }

            _logger.LogInformation("[LivePositionManager] Restoring snapshots for: {Accounts}", string.Join(", ", accountKeys));

            foreach (var acc in accountKeys)
            {
                LoadSnapshot(acc);
            }
        }

        private void LoadSnapshot(string accountKey)
        {
            var list = _snapshotStore.LoadPositions(accountKey);
            if (list.Count == 0) return;

            foreach (var pos in list)
            {
                // [Step 3] 如果快照有 MarketNo，warm cache
                if (pos.MarketNo != MarketNo.Unknown)
                {
                    _marketInfoProvider.UpsertFromSnapshot(pos.StockId, pos.MarketNo, pos.Source);
                }

                _positions[(accountKey, pos.StockId)] = pos with { AccountKey = accountKey, Account = accountKey };
            }
        }

        private void MarkSnapshotDirty(string accountKey)
        {
            if (string.IsNullOrWhiteSpace(accountKey)) return;

            lock (_snapshotLock)
            {
                _snapshotDirty = true;
                _snapshotDirtyAccounts.Add(accountKey);
            }
            _snapshotTimer.Stop();
            _snapshotTimer.Start();
        }

        private void FlushSnapshots()
        {
            List<string> accounts;

            lock (_snapshotLock)
            {
                if (!_snapshotDirty) return;
                _snapshotDirty = false;
                accounts = _snapshotDirtyAccounts.ToList();
                _snapshotDirtyAccounts.Clear();
            }

            foreach (var accountKey in accounts)
            {
                // Strict Check: Do not save Real snapshot in Sim mode
                if (_isSimMode && accountKey.Equals("Real", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip
                }

                var positionsForAccount = _positions.Values
                    .Where(p => string.Equals(p.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));

                // [Step 3] Ensure MarketNo
                var positionsWithMarketInfo = positionsForAccount.Select(p =>
                {
                    if (p.MarketNo != MarketNo.Unknown)
                        return p;

                    if (_marketInfoProvider.TryGetMarketInfo(p.StockId, out var info))
                    {
                        return p with { MarketNo = info.MarketNo, Source = info.Source };
                    }

                    WarnSnapshotMarketNoMissingThrottled(p.StockId);
                    return p;
                }).ToList();

                _snapshotStore.SavePositions(accountKey, positionsWithMarketInfo);
            }
        }

        private readonly ConcurrentDictionary<string, long> _lastPosQuoteUpdateTicks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly long PosQuoteMinIntervalTicks = TimeSpan.FromMilliseconds(150).Ticks; // 節流避免爆量

        private void OnQuoteSnapshot(QuoteSnapshotEvent e)
        {
            if (e == null) return;
            if (string.IsNullOrWhiteSpace(e.Symbol)) return;
            if (e.LastPrice <= 0) return; // 沒價就不更新

            // 節流：同一檔 150ms 內最多更新一次（你可以調 100~300ms）
            var now = DateTime.UtcNow.Ticks;
            var last = _lastPosQuoteUpdateTicks.GetOrAdd(e.Symbol, 0);
            if (now - last < PosQuoteMinIntervalTicks) return;
            _lastPosQuoteUpdateTicks[e.Symbol] = now;

            var anyChanged = false;

            // 只更新「有這檔持倉」的 entries
            foreach (var kvp in _positions) // 你的 _positions 是 ConcurrentDictionary<(acc,symbol), Position> 或類似
            {
                var pos = kvp.Value;
                if (!string.Equals(pos.StockId, e.Symbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (pos.LastPrice != e.LastPrice)
                {
                    _positions[kvp.Key] = pos with { LastPrice = e.LastPrice };
                    anyChanged = true;
                }
            }

            if (!anyChanged) return;

            RecalculateAllPositions();
            BroadcastSnapshot(allowEmpty: true);   // 這行在舊版 tick 流程就是這樣做的精神
            MarkSnapshotDirty(ResolveDefaultAccount()); // Ensure persistence updates
        }

        private void OnBrokerSnapshot(BrokerPositionSnapshotEvent evt)
        {
            if (evt == null || evt.Positions == null) return;
            // Use AccountKey from event, pass BrokerAccountNo for logging if available
            ApplyBrokerSnapshot(evt.AccountKey, evt.Positions, evt.IsFinal, evt.Scope, evt.CorrelationId, evt.BrokerAccountNo);
        }

        private void OnBrokerPositionMetrics(BrokerPositionMetricsEvent evt)
        {
            if (evt?.Metrics == null || evt.Metrics.Count == 0) return;

            var touchedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updated = false;

            foreach (var metric in evt.Metrics)
            {
                if (string.IsNullOrWhiteSpace(metric.Symbol) || metric.Quantity == 0) continue;

                var accountKey = MuNTrade.Core.Capital.CapitalConfigService.ResolveAccountKey(metric.Account);
                if (string.IsNullOrWhiteSpace(accountKey) || accountKey.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    accountKey = ResolveDefaultAccount();
                }

                var existing = GetPositionInternal(accountKey, metric.Symbol);
                var avgCost = metric.AverageCost.GetValueOrDefault(existing.AveragePrice);
                var lastPrice = metric.MarketPrice.GetValueOrDefault(existing.LastPrice);
                var qty = existing.Quantity != 0 ? existing.Quantity : metric.Quantity;
                if (qty == 0) continue;

                var info = _marketInfoProvider.GetMarketInfoOrThrow(metric.Symbol, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);
                var merged = existing with
                {
                    StockId = metric.Symbol,
                    Account = accountKey,
                    AccountKey = accountKey,
                    Quantity = qty,
                    AveragePrice = avgCost > 0 ? avgCost : existing.AveragePrice,
                    LastPrice = lastPrice > 0 ? lastPrice : existing.LastPrice,
                    UnrealizedPnL = metric.UnrealizedPnL.GetValueOrDefault(existing.UnrealizedPnL),
                    BreakEvenPrice = metric.BreakEvenPrice.GetValueOrDefault(existing.BreakEvenPrice),
                    MarketNo = existing.MarketNo != MarketNo.Unknown ? existing.MarketNo : info.MarketNo,
                    Source = existing.Source != MarketSource.Domestic ? existing.Source : info.Source
                };

                _positions[(accountKey, metric.Symbol)] = merged;
                touchedAccounts.Add(accountKey);
                updated = true;
            }

            if (!updated) return;

            RecalculateAllPositions();
            BroadcastSnapshot(allowEmpty: true);
            foreach (var accountKey in touchedAccounts)
            {
                MarkSnapshotDirty(accountKey);
            }

            _logger.LogInformation("[LivePositionManager] Applied broker position metrics for {Count} symbols across {Accounts} account(s).",
                evt.Metrics.Count, touchedAccounts.Count);
        }

        private string ResolveDefaultAccount()
        {
            // V2: Return Account Key
            return _isSimMode ? "Sim:Paper:All:Default" : "Real";
        }

        private readonly ConcurrentDictionary<string, long> _snapWarnTicks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly long SnapWarnIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        private void WarnSnapshotMarketNoMissingThrottled(string symbol)
        {
            var now = DateTime.UtcNow.Ticks;
            var last = _snapWarnTicks.GetOrAdd(symbol, 0);
            if (now - last <= SnapWarnIntervalTicks) return;
            _snapWarnTicks[symbol] = now;

            var msg = $"[PositionSnapshot] 無法取得 MarketNo (Symbol: {symbol}) - 尚未訂閱行情或快照尚未帶入。將暫存為 Unknown。";
            _logger.LogWarning(msg);
#if DEBUG
            System.Diagnostics.Debug.WriteLine(msg);
#endif
        }

        private bool IsStockSymbol(string symbol, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose purpose)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

            if (purpose is MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Risk or MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution)
            {
                return !_marketInfoProvider.GetMarketInfoOrThrow(symbol, purpose).IsDerivatives;
            }

            if (_marketInfoProvider.TryGetMarketInfo(symbol, out var info))
            {
                return !info.IsDerivatives;
            }
            return false;
        }

        private decimal GetTickSizeForSymbol(string symbol, decimal price, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose purpose)
        {
            if (IsStockSymbol(symbol, purpose))
            {
                return _priceTierService.GetTickPrice(price);
            }
            else
            {
                return 1m;
            }
        }

        private void RecalculateAllPositions()
        {
            foreach (var kvp in _positions)
            {
                var pos = kvp.Value;
                var multiplier = GetMultiplier(pos.StockId, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);

                decimal lastPrice = pos.LastPrice == 0 ? pos.AveragePrice : pos.LastPrice;
                bool isLong = pos.Quantity > 0;
                
                // [Net PnL] = Gross - (CostIn + CostOut)
                decimal grossPnL = (lastPrice - pos.AveragePrice) * pos.Quantity * multiplier;
                decimal costIn = EstimateOneSideCost(pos.StockId, pos.AveragePrice, pos.Quantity, true, isLong, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);
                decimal costOut = EstimateOneSideCost(pos.StockId, lastPrice, pos.Quantity, false, isLong, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);
                decimal totalCost = costIn + costOut;
                decimal netPnL = grossPnL - totalCost;

                decimal bep = CalculateBreakEven(pos.StockId, pos.AveragePrice, pos.Quantity);

                var newPos = pos with
                {
                    UnrealizedPnL = netPnL,
                    EstimatedTotalCost = totalCost,
                    BreakEvenPrice = bep
                };

                _positions[kvp.Key] = newPos;
            }
        }

        private decimal GetMultiplier(string symbol, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose purpose)
        {
            if (string.IsNullOrEmpty(symbol)) return 1;

            if (_tradeRulesProvider.Current.Futures.TryGetValue(symbol, out var f)) return f.PointValue;

            if (purpose is MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Risk or MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution)
            {
                var strictInfo = _marketInfoProvider.GetMarketInfoOrThrow(symbol, purpose);
                return strictInfo.IsDerivatives ? 1 : 1000;
            }

            if (!_marketInfoProvider.TryGetMarketInfo(symbol, out var info))
            {
                return 0;
            }

            return info.IsDerivatives ? 1 : 1000;
        }

        private decimal EstimateOneSideCost(string symbol, decimal price, long qty, bool isEntry, bool isLong, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose purpose)
        {
            decimal multiplier = GetMultiplier(symbol, purpose);
            if (multiplier == 0) return 0;

            decimal value = price * Math.Abs(qty) * multiplier;
            var rules = _tradeRulesProvider.Current;

            bool isDerivatives = false;
            if (purpose is MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Risk or MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution)
            {
                isDerivatives = _marketInfoProvider.GetMarketInfoOrThrow(symbol, purpose).IsDerivatives;
            }
            else
            {
                if (!_marketInfoProvider.TryGetMarketInfo(symbol, out var info)) return 0;
                isDerivatives = info.IsDerivatives;
            }

            if (!isDerivatives)
            {
                // [Stock] Fee = Value * 0.001425 * Discount
                decimal fee = Math.Floor(value * 0.001425m * (rules.Stock?.FeeDiscount ?? 1m));
                
                // [Stock] Tax = Value * TaxRate (Only on SELL side)
                // Sell side happens when: (Long + !isEntry) OR (!Long + isEntry)
                bool isSell = (isLong && !isEntry) || (!isLong && isEntry);
                decimal tax = 0;
                if (isSell)
                {
                    // Check if it's DayTrade for tax discount
                    // Note: In this context, we don't have the entry time easily, 
                    // but for general estimation, we use the default TaxRate.
                    // If we want to be more precise, we'd need more context here.
                    tax = Math.Floor(value * (rules.Stock?.TaxRate ?? 0.003m));
                }
                
                return fee + tax;
            }
            else // Futures
            {
                var f = ResolveFutureSpec(symbol, rules);
                if (f != null)
                {
                    // 修正：手續費為雙邊 (round-trip)，因此單邊需除以 2。
                    // 使用者表示手續費已包含期交稅，因此 TaxRate 在設定檔中應設為 0。
                    var tax = Math.Floor(price * qty * f.PointValue * (f.TaxRate > 0 ? f.TaxRate : 0));
                    return (f.FeePerOrder / 2m * Math.Abs(qty)) + tax;
                }

                // Default fallback
                return (25m * Math.Abs(qty)) + Math.Floor(price * qty * 200m * 0);
            }
        }

        private MuNTrade.Abstractions.Domain.Capital.FutureContractSpec? ResolveFutureSpec(string symbol, MuNTrade.Abstractions.Domain.Capital.TradeRulesConfig rules)
        {
            if (rules.Futures == null) return null;

            // 1. Exact match (e.g., TX00, TX03)
            if (rules.Futures.TryGetValue(symbol, out var spec)) return spec;

            // 2. Prefix match for common futures
            // Mapping known prefixes to their generic settings
            if (symbol.StartsWith("MTX") || symbol.StartsWith("MX"))
            {
                if (rules.Futures.TryGetValue("MTX00", out var mtx)) return mtx;
            }
            if (symbol.StartsWith("TX") && !symbol.StartsWith("TXF")) // TX but not TXF (often used for specific month codes)
            {
                if (rules.Futures.TryGetValue("TX00", out var tx)) return tx;
            }
            if (symbol.StartsWith("TM"))
            {
                if (rules.Futures.TryGetValue("TM00", out var tm)) return tm;
            }

            // 3. Fallback: try to find any part of the key
            return rules.Futures.Values.FirstOrDefault(v => symbol.Contains(v.Name));
        }

        private decimal CalculateBreakEven(string symbol, decimal avgPrice, long qty)
        {
            if (qty == 0) return 0;
            bool isLong = qty > 0;
            decimal multiplier = GetMultiplier(symbol, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);
            if (multiplier == 0) return avgPrice;

            decimal costIn = EstimateOneSideCost(symbol, avgPrice, qty, true, isLong, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);
            decimal costOut = EstimateOneSideCost(symbol, avgPrice, qty, false, isLong, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI);

            decimal totalCost = costIn + costOut;
            decimal pointsNeeded = totalCost / (Math.Abs(qty) * multiplier);

            decimal rawBep = isLong ? avgPrice + pointsNeeded : avgPrice - pointsNeeded;

            decimal tick = IsStockSymbol(symbol, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.UI) ? _priceTierService.GetTickSizeAtPrice(rawBep) : 1m;
            if (tick == 0) return rawBep;

            return isLong
                ? Math.Ceiling(rawBep / tick) * tick
                : Math.Floor(rawBep / tick) * tick;
        }

        public void UpdatePositionOnFill(OrderFillEvent fill)
        {
            if (string.IsNullOrEmpty(fill.StockId)) return;

            var accountKey = fill.AccountKey;
            if (string.IsNullOrWhiteSpace(accountKey))
            {
                accountKey = MuNTrade.Core.Capital.CapitalConfigService.ResolveAccountKey(fill.Account);
                if (string.IsNullOrWhiteSpace(accountKey) || accountKey == "Unknown")
                {
                    accountKey = "Real";
                }
            }

            var key = (Account: accountKey, Symbol: fill.StockId);
            var multiplier = GetMultiplier(fill.StockId, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution);

            bool updated = false;

            var oldPos = GetPositionInternal(accountKey, fill.StockId);
            long oldQty = oldPos.Quantity;
            decimal oldAvg = oldPos.AveragePrice;

            long fillQtySigned = fill.Side == OrderSide.Buy ? fill.FillQuantity : -fill.FillQuantity;
            long newQty = oldQty + fillQtySigned;

            if (newQty == 0)
            {
                _positions.TryRemove(key, out _);
                _logger.LogInformation($"[{accountKey}] 平倉: {fill.StockId}");

                updated = true;
            }
            else
            {
                decimal newAvg;
                if (oldQty == 0 || Math.Sign(newQty) != Math.Sign(oldQty)) newAvg = fill.FillPrice;
                else if (Math.Sign(fillQtySigned) == Math.Sign(oldQty))
                    newAvg = ((oldAvg * Math.Abs(oldQty)) + (fill.FillPrice * Math.Abs(fillQtySigned))) / Math.Abs(newQty);
                else newAvg = oldAvg;

                decimal costIn = EstimateOneSideCost(fill.StockId, newAvg, newQty, true, newQty > 0, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution);
                decimal costOut = EstimateOneSideCost(fill.StockId, fill.FillPrice, newQty, false, newQty > 0, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution);
                decimal totalCost = costIn + costOut;
                decimal grossPnL = (fill.FillPrice - newAvg) * newQty * multiplier;
                decimal netPnL = grossPnL - totalCost;

                decimal bep = CalculateBreakEven(fill.StockId, newAvg, newQty);

                string stratId = oldPos.StrategyId;
                string accKey = oldPos.AccountKey;
                string entryCorr = oldPos.EntryCorrelationId;

                if (oldQty == 0 || Math.Sign(newQty) != Math.Sign(oldQty))
                {
                    stratId = fill.StrategyId ?? "Unknown";
                    accKey = fill.AccountKey ?? "Unknown";
                    entryCorr = fill.CorrelationId ?? "Unknown";
                }

                var info = _marketInfoProvider.GetMarketInfoOrThrow(fill.StockId, MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Execution);

                var newPos = new Position
                {
                    StockId = fill.StockId,
                    Account = accountKey,
                    AccountKey = accountKey,
                    Quantity = newQty,
                    AveragePrice = newAvg,
                    LastPrice = fill.FillPrice,
                    EntryTime = oldPos.EntryTime == DateTime.MinValue ? fill.FillTime : oldPos.EntryTime,
                    UnrealizedPnL = netPnL,
                    EstimatedTotalCost = totalCost,
                    BreakEvenPrice = bep,
                    StrategyId = stratId,
                    EntryCorrelationId = entryCorr,
                    MarketNo = info.MarketNo,
                    Source = info.Source
                };
                _positions[key] = newPos;
                updated = true;

                if (newPos.Quantity != 0 && Math.Abs(newPos.Quantity) < 0.001m)
                {
                    _logger.LogWarning("[Position] Residual position detected! Key={Key}, Sym={Symbol}, Qty={Qty}", accountKey, fill.StockId, newPos.Quantity);
                }
            }

            if (updated)
            {
                BroadcastSnapshot();
                MarkSnapshotDirty(accountKey);
            }
        }

        private void BroadcastSnapshot(bool allowEmpty = false)
        {
            if (_positions.IsEmpty && !allowEmpty) return;
            var dtos = _positions.Values
            .Where(p => p.Quantity != 0)
            .Select(p => new PositionDto
            {
                Account = p.Account,
                AccountKey = p.AccountKey,
                Symbol = p.StockId,
                Side = p.Quantity > 0 ? OrderSide.Buy : OrderSide.Sell,
                Quantity = Math.Abs(p.Quantity),
                AverageCost = p.AveragePrice,
                LastPrice = p.LastPrice,
                UnrealizedPnL = p.UnrealizedPnL,
                EstimatedTotalCost = p.EstimatedTotalCost,
                BreakEvenPrice = p.BreakEvenPrice,
                MarketNo = p.MarketNo,
                Source = p.Source
            }).ToList();
            _bus.Publish(new PositionSnapshotEvent { Positions = dtos });
        }

        public Position GetCurrentPosition(string stockId) => GetPositionInternal(ResolveDefaultAccount(), stockId);
        public IEnumerable<Position> GetAllOpenPositions() => _positions.Values.Where(p => p.Quantity != 0);
        public Task<Position> GetPositionAsync(string symbol) => Task.FromResult(GetCurrentPosition(symbol));
        public bool HasPosition(string symbol) => _positions.Values.Any(p => p.StockId == symbol);
        public void ProcessSignal(SignalEvent signal) { }
        private Position GetPositionInternal(string account, string stockId) =>
            _positions.TryGetValue((account, stockId), out var p) ? p : new Position { StockId = stockId, Account = account, AccountKey = account };

        public void Dispose()
        {
            FlushSnapshots();
            _broadcastTimer?.Dispose();
            _snapshotTimer?.Dispose();
            _brokerSnapshotToken?.Dispose();
            _brokerMetricsToken?.Dispose();

            if (_tradeRulesProvider != null) _tradeRulesProvider.Changed -= OnRulesChanged;
        }
    }
}

