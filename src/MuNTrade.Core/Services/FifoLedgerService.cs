using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Events.Capital; // For SimAccountChangedEvent
using MuNTrade.Abstractions.Domain.Capital; // For SimAccountModel
using MuNTrade.Core.Capital;

namespace MuNTrade.Core.Services
{
    public sealed class FifoLedgerService : IDisposable
    {
        private const string UnknownStrategyId = "UnknownStrategy";
        private const string MixedStrategyId = "Mixed";

        private readonly IEventBus _bus;
        private readonly ILogger<FifoLedgerService> _logger;
        private readonly IClock _clock;
        private readonly ITradeRulesProvider _tradeRulesProvider;
        private readonly IMarketInfoProvider _marketInfoProvider;
        private readonly ITradingSessionProvider _sessionProvider;

        private readonly object _sync = new();
        private readonly object _fileLock = new();
        private readonly Dictionary<string, Dictionary<string, Queue<OpenLot>>> _openLots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Account, string Symbol), TradeRound> _rounds = new();
        private readonly HashSet<string> _dirtyAccounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Timers.Timer _flushTimer;
        private bool _openLotsDirty;
        private bool _isSimMode;

        // Deduplication to prevent SKCOM historical replay from double-counting PnL
        private readonly HashSet<string> _processedFills = new(StringComparer.OrdinalIgnoreCase);

        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly JsonSerializerOptions _jsonlOptions = new() { WriteIndented = false };
        private readonly MuNTrade.Abstractions.Contracts.Capital.ICapitalStateRepository _stateRepo;

        public FifoLedgerService(
            IEventBus bus,
            ILogger<FifoLedgerService> logger,
            IClock clock,
            ITradeRulesProvider tradeRulesProvider,
            IMarketInfoProvider marketInfoProvider,
            ITradingSessionProvider sessionProvider,
            IOptions<ApplicationSettings> appSettings,
            MuNTrade.Abstractions.Contracts.Capital.ICapitalStateRepository stateRepo)
        {
            _bus = bus;
            _logger = logger;
            _clock = clock;
            _tradeRulesProvider = tradeRulesProvider;
            _marketInfoProvider = marketInfoProvider;
            _sessionProvider = sessionProvider;
            _stateRepo = stateRepo;

            var gateway = appSettings.Value.TradingGateway?.ToUpperInvariant() ?? "PAPER";
            _isSimMode = gateway.Contains("PAPER") || gateway.Contains("SIM");

            _bus.Subscribe<OrderFillEvent>(OnOrderFill);
            _bus.Subscribe<GatewayModeChangedEvent>(OnGatewayModeChanged);

            _flushTimer = new System.Timers.Timer(1000) { AutoReset = false };
            _flushTimer.Elapsed += (s, e) => FlushOpenLots();

            LoadOpenLotsForMode();
            LoadTodayProcessedFills();
        }

        private void OnGatewayModeChanged(GatewayModeChangedEvent evt)
        {
            FlushOpenLots();

            lock (_sync)
            {
                _openLots.Clear();
                _rounds.Clear();
                _dirtyAccounts.Clear();
            }

            var gateway = evt.Mode.ToUpperInvariant();
            _isSimMode = gateway.Contains("PAPER") || gateway.Contains("SIM");
            LoadOpenLotsForMode();
        }

        private void OnOrderFill(OrderFillEvent fill)
        {
            if (fill == null || string.IsNullOrWhiteSpace(fill.StockId) || fill.FillQuantity <= 0) return;

            var accountKey = ResolveAccountKey(fill);

            // Deduplication Check
            // Use ExchangeTs (ticks) + StockId + Side + Qty + Price (+ OrderId if available as fallback context)
            var signature = $"{fill.StockId}_{fill.Side}_{fill.FillQuantity}_{fill.FillPrice}_{fill.FillTime.Ticks}_{fill.OrderId}";
            lock (_sync)
            {
                if (!_processedFills.Add(signature))
                {
                    _logger.LogDebug("[FifoLedger] Ignoring duplicate historical fill: {Sig}", signature);
                    return;
                }
            }

            // 放寬模式攔截：不再因為 _isSimMode == true 且 accountKey == "Real" 就直接 return 丟棄實盤交易紀錄

            var fillId = Guid.NewGuid().ToString("N");
            var marketKind = ResolveMarketKind(fill.StockId);
            var fillFee = EstimateFillFee(fill.StockId, fill.FillPrice, fill.FillQuantity, marketKind);
            var fillTax = EstimateFillTax(fill.StockId, fill.FillPrice, fill.FillQuantity, fill.Side, marketKind);

            AppendFillHistory(accountKey, new FillHistoryRecord
            {
                FillId = fillId,
                OrderId = fill.OrderId ?? string.Empty,
                Time = fill.FillTime,
                AccountKey = accountKey,
                Symbol = fill.StockId,
                Side = fill.Side,
                Qty = fill.FillQuantity,
                Price = fill.FillPrice,
                Fee = fillFee,
                Tax = fillTax,
                MarketKind = marketKind
            });

            var legsToPublish = new List<RealizedLeg>();
            CompletedTrade? completedTrade = null;

            lock (_sync)
            {
                var symbolLots = GetOrCreateSymbolLots(accountKey, fill.StockId);
                var queue = symbolLots.Queue;
                var remaining = fill.FillQuantity;

                if (queue.Count > 0 && !IsSameSide(queue.Peek().RemainingQty, fill.Side))
                {
                    var closeQty = 0L;
                    var matched = new List<MatchedLot>();
                    decimal grossPnL = 0m;
                    decimal fee = 0m;
                    decimal tax = 0m;
                    var multiplier = ResolveMultiplier(fill.StockId, marketKind);

                    while (remaining > 0 && queue.Count > 0)
                    {
                        var lot = queue.Peek();
                        var lotQtyAbs = Math.Abs(lot.RemainingQty);
                        var matchQty = Math.Min(lotQtyAbs, remaining);
                        var lotSign = Math.Sign(lot.RemainingQty);

                        if (matchQty <= 0) break;

                        var pnlPerUnit = lotSign > 0
                            ? fill.FillPrice - lot.EntryPrice
                            : lot.EntryPrice - fill.FillPrice;

                        grossPnL += pnlPerUnit * matchQty * multiplier;
                        fee += EstimateFillFee(fill.StockId, fill.FillPrice, matchQty, marketKind);
                        tax += EstimateExitTax(fill.StockId, fill.FillPrice, matchQty, lot.EntryTime, fill.FillTime, marketKind);

                        matched.Add(new MatchedLot
                        {
                            LotId = lot.LotId,
                            EntryTime = lot.EntryTime,
                            EntryPrice = lot.EntryPrice,
                            MatchedQty = matchQty,
                            SourceFillId = lot.SourceFillId,
                            EntryStrategyId = lot.EntryStrategyId,
                            EntryCorrelationId = lot.EntryCorrelationId
                        });

                        closeQty += matchQty;
                        remaining -= matchQty;

                        lot.RemainingQty = lotSign > 0 ? lot.RemainingQty - matchQty : lot.RemainingQty + matchQty;
                        if (lot.RemainingQty == 0)
                        {
                            queue.Dequeue();
                        }
                    }

                    if (closeQty > 0)
                    {
                        var attribution = ResolveAttribution(matched);
                        var leg = new RealizedLeg
                        {
                            LegId = Guid.NewGuid().ToString("N"),
                            AccountKey = accountKey,
                            Account = fill.Account,
                            StockId = fill.StockId,
                            ExitSide = fill.Side,
                            CloseQty = closeQty,
                            ExitPrice = fill.FillPrice,
                            ExitTime = fill.FillTime,
                            ExitFillId = fillId,
                            MatchedLots = matched,
                            GrossPnL = grossPnL,
                            Fee = fee,
                            Tax = tax,
                            NetPnL = grossPnL - fee - tax,
                            StrategyId = attribution.StrategyId,
                            ExitInitiatorStrategyId = fill.StrategyId ?? string.Empty,
                            AttributionConflict = attribution.Conflict,
                            CorrelationId = fill.CorrelationId ?? string.Empty
                        };

                        legsToPublish.Add(leg);
                        UpdateRoundAccumulator(accountKey, fill.StockId, fill.Account, fill.FillTime, fillId, leg);
                    }
                }

                if (remaining > 0)
                {
                    var signedQty = fill.Side == OrderSide.Buy ? remaining : -remaining;
                    queue.Enqueue(new OpenLot
                    {
                        LotId = fillId,
                        EntryTime = fill.FillTime,
                        EntryPrice = fill.FillPrice,
                        RemainingQty = signedQty,
                        SourceFillId = fillId,
                        EntryStrategyId = string.IsNullOrWhiteSpace(fill.StrategyId) ? UnknownStrategyId : fill.StrategyId,
                        EntryCorrelationId = fill.CorrelationId ?? string.Empty
                    });
                }

                if (queue.Count == 0)
                {
                    symbolLots.Map.Remove(fill.StockId);
                    if (TryFinalizeRound(accountKey, fill.StockId, out completedTrade))
                    {
                        // completedTrade is set
                    }
                }

                MarkOpenLotsDirty(accountKey);
            }

            foreach (var leg in legsToPublish)
            {
                AppendLeg(accountKey, leg);
                _bus.Publish(new RealizedLegCreatedEvent { Leg = leg });
            }

            if (completedTrade != null)
            {
                _logger.LogInformation("[E2E][Ledger] CompletedTrade emitted. id={Id} netPnl={NetPnL:N0} sym={Sym} qty={Qty}",
                    completedTrade.ExitCorrelationId, completedTrade.NetPnL, completedTrade.StockId, completedTrade.Quantity);
                _bus.Publish(completedTrade);

                if (completedTrade.AccountKey.StartsWith("Sim:", StringComparison.OrdinalIgnoreCase) || 
                   (!completedTrade.AccountKey.Equals("Real", StringComparison.OrdinalIgnoreCase) && _isSimMode))
                {
                    UpdateSimAccountBalance(completedTrade);
                }
                
                if (completedTrade.AccountKey.Equals("Real", StringComparison.OrdinalIgnoreCase) || 
                    (!_isSimMode && !completedTrade.AccountKey.StartsWith("Sim:", StringComparison.OrdinalIgnoreCase)))
                {
                    _ = UpdateLiveAccountBalanceAsync(completedTrade);
                }
            }
        }

        private async System.Threading.Tasks.Task UpdateLiveAccountBalanceAsync(CompletedTrade trade)
        {
            try
            {
                var state = await _stateRepo.LoadAsync();
                var account = state.Accounts?.FirstOrDefault(a => a.Id == state.ActiveAccountId) ?? state.Accounts?.FirstOrDefault();
                
                if (account == null)
                {
                    _logger.LogWarning("[FifoLedger] No live account found in CapitalStateRepository to update PnL.");
                    return;
                }

                var now = _clock.Now;
                DateOnly tradeDateOnly;
                DateTime tradeDate;
                var session = _sessionProvider.GetSession("TX00", now);
                if (session != null)
                {
                    tradeDateOnly = session.TradeDate;
                    tradeDate = tradeDateOnly.ToDateTime(TimeOnly.MinValue);
                }
                else
                {
                    tradeDate = GetVirtualTradingDate(now);
                    tradeDateOnly = DateOnly.FromDateTime(tradeDate);
                }

                account.RealizedPnL += trade.NetPnL;

                // [Fix] 實盤也套用與昨日切分的規則，計算「今日」已實現損益
                bool isFutures = _tradeRulesProvider.Current.Futures.ContainsKey(trade.StockId ?? "") ||
                                 trade.StockId?.StartsWith("TX") == true ||
                                 trade.StockId?.StartsWith("MTX") == true;

                if (account.DayPnLDate != tradeDateOnly)
                {
                    account.DayPnLDate = tradeDateOnly;
                    account.StockDayRealizedPnL = 0;
                    account.FuturesDayRealizedPnL = 0;
                }

                if (isFutures)
                {
                    account.FuturesDayRealizedPnL += trade.NetPnL;
                }
                else
                {
                    account.StockDayRealizedPnL += trade.NetPnL;
                }
                // Sync back
                await _stateRepo.SaveAsync(state);
                _logger.LogInformation("[FifoLedger] Updated LIVE account {Id} RealizedPnL: {PnL:N0} -> Total Realized: {Tot:N0}", account.Id, trade.NetPnL, account.RealizedPnL);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to update live account balance.");
            }
        }

        private void UpdateSimAccountBalance(CompletedTrade trade)
        {
            try
            {
                // 1. Load Account
                var accounts = CapitalConfigService.LoadAllSimAccounts();

                // Strategy 1: Exact ID Match
                var acc = accounts.FirstOrDefault(a => string.Equals(a.Id, trade.AccountKey, StringComparison.OrdinalIgnoreCase));

                // Strategy 2: Fuzzy Match (If Key is "Sim:Paper:All:Default")
                if (acc == null)
                {
                    bool isDefaultKey = trade.AccountKey.Contains("Default", StringComparison.OrdinalIgnoreCase)
                                     || trade.AccountKey.Contains("Paper", StringComparison.OrdinalIgnoreCase);

                    if (isDefaultKey)
                    {
                        // Try finding account with "Default" or "Paper" in name
                        acc = accounts.FirstOrDefault(a => a.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)
                                                        || a.Name.Contains("Paper", StringComparison.OrdinalIgnoreCase));
                        if (acc != null)
                        {
                            _logger.LogInformation("[FifoLedger] Account Mapping Fallback: Found account {Id} ({Name}) by fuzzy name match for trade key {Key}", acc.Id, acc.Name, trade.AccountKey);
                        }
                    }
                }

                // Strategy 3: Single Account Fallback
                if (acc == null && accounts.Count == 1)
                {
                    acc = accounts[0];
                    _logger.LogInformation("[FifoLedger] Account Mapping Fallback: Using the only available account {Id} ({Name}) for trade key {Key}", acc.Id, acc.Name, trade.AccountKey);
                }

                if (acc == null)
                {
                    _logger.LogWarning("[FifoLedger] Failed to find SimAccount for key '{Key}'. Available: {List}",
                        trade.AccountKey,
                        string.Join(", ", accounts.Select(a => $"{a.Id}({a.Name})")));
                    return;
                }

                // 2. Resolve Market Kind
                bool isFutures = _tradeRulesProvider.Current.Futures.ContainsKey(trade.StockId) ||
                                 trade.StockId.StartsWith("TX") ||
                                 trade.StockId.StartsWith("MTX");

                // 3. Update Balance
                if (isFutures)
                {
                    acc.FuturesBalance += trade.NetPnL;
                    _logger.LogInformation("[FifoLedger] Updated FuturesBalance for {Name}: {Old} -> {New} (PnL: {PnL})", acc.Name, acc.FuturesBalance - trade.NetPnL, acc.FuturesBalance, trade.NetPnL);
                }
                else
                {
                    acc.StockBalance += trade.NetPnL;
                    _logger.LogInformation("[FifoLedger] Updated StockBalance for {Name}: {Old} -> {New} (PnL: {PnL})", acc.Name, acc.StockBalance - trade.NetPnL, acc.StockBalance, trade.NetPnL);
                }

                // 3.5 Update Daily Realized PnL (State-based)
                // This ensures PnL is tracked persistently and survives restarts.
                // [Fix] Use Official Trading Session or Virtual Date fallback
                var now = _clock.Now;
                DateOnly tradeDateOnly;
                DateTime tradeDate;

                var session = _sessionProvider.GetSession("TX00", now);
                string sourceLedger;
                // If we are in a valid session, define trade date by session.
                if (session != null)
                {
                    tradeDateOnly = session.TradeDate;
                    tradeDate = tradeDateOnly.ToDateTime(TimeOnly.MinValue);
                    sourceLedger = "session";
                }
                else
                {
                    // Fallback to virtual logic (05:00 AM reset) if outside session (e.g. gap time)
                    tradeDate = GetVirtualTradingDate(now);
                    tradeDateOnly = DateOnly.FromDateTime(tradeDate);
                    sourceLedger = "fallback";
                }

                if (isFutures)
                {
                    if (acc.FuturesDayPnLDate != tradeDateOnly)
                    {
                        _logger.LogInformation("[PnL.State][Finalize] Reset FuturesDayRealizedPnL for {Name}. Old={Old:N0} TradeDate={Date}",
                            acc.Name, acc.FuturesDayRealizedPnL, tradeDateOnly.ToString("yyyy-MM-dd"));
                        acc.FuturesDayRealizedPnL = 0;
                        acc.FuturesDayPnLDate = tradeDateOnly;
                    }
                    acc.FuturesDayRealizedPnL += trade.NetPnL;
                    _logger.LogInformation("[PnL.State][Finalize] tradeDate={Date} mkt=Derivatives netPnl={PnL:N0} -> FuturesDayRealizedPnL={Val:N0}",
                        tradeDateOnly.ToString("yyyy-MM-dd"), trade.NetPnL, acc.FuturesDayRealizedPnL);
                }
                else
                {
                    if (acc.StockDayPnLDate != tradeDateOnly)
                    {
                        _logger.LogInformation("[PnL.State][Finalize] Reset StockDayRealizedPnL for {Name}. Old={Old:N0} TradeDate={Date}",
                            acc.Name, acc.StockDayRealizedPnL, tradeDateOnly.ToString("yyyy-MM-dd"));
                        acc.StockDayRealizedPnL = 0;
                        acc.StockDayPnLDate = tradeDateOnly;
                    }
                    acc.StockDayRealizedPnL += trade.NetPnL;
                    _logger.LogInformation("[PnL.State][Finalize] tradeDate={Date} mkt=Securities netPnl={PnL:N0} -> StockDayRealizedPnL={Val:N0}",
                        tradeDateOnly.ToString("yyyy-MM-dd"), trade.NetPnL, acc.StockDayRealizedPnL);
                }

                // Backward-compatible total (same trade date only)
                var stockForTotal = acc.StockDayPnLDate == tradeDateOnly ? acc.StockDayRealizedPnL : 0m;
                var futuresForTotal = acc.FuturesDayPnLDate == tradeDateOnly ? acc.FuturesDayRealizedPnL : 0m;
                acc.DayRealizedPnL = stockForTotal + futuresForTotal;
                acc.DayPnLDate = tradeDate;

                UpdateDailyLossUsed(acc, trade, tradeDateOnly, isFutures, sourceLedger);

                // 4. Save
                CapitalConfigService.SaveSimAccount(acc, _clock);
                _logger.LogInformation("[E2E][Ledger] SimAccount saved. id={Id} lastUpdated={Updated:yyyy-MM-dd HH:mm:ss}", acc.Id, acc.LastUpdated);

                // 5. Broadcast Update
                // Passing ID is safer as it is unique. UI/Services should handle ID lookup.
                _bus.Publish(new SimAccountChangedEvent(acc.Id));
                _logger.LogInformation("[E2E][Ledger] SimAccountChangedEvent published. accountKey={Key}", acc.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to update sim account balance logic exception.");
            }
        }
        private static DateTime GetVirtualTradingDate(DateTime now)
        {
            // [Correction] Taiwan Futures Trading Day Logic
            // The trading day switches at 15:00 (start of Night Session).
            // So 15:00 today counts as TOMORROW's trading day.
            // 00:00 to 14:59:59 counts as TODAY's trading day.
            if (now.TimeOfDay >= new TimeSpan(15, 0, 0))
            {
                return now.Date.AddDays(1);
            }
            return now.Date;
        }

        private void UpdateDailyLossUsed(SimAccountModel acc, CompletedTrade trade, DateOnly tradeDate, bool isFutures, string sourceLedger)
        {
            if (trade.NetPnL >= 0) return;

            var loss = Math.Abs(trade.NetPnL);
            if (isFutures)
            {
                if (acc.FuturesDailyLossTradeDate != tradeDate)
                {
                    _logger.LogWarning("[TradeDate][Mismatch] component=Ledger mkt=Futures tradeDate={TradeDate} frozenDate={FrozenDate} sourceLedger={Source}",
                        tradeDate.ToString("yyyy-MM-dd"), acc.FuturesDailyLossTradeDate.ToString("yyyy-MM-dd"), sourceLedger);
                    return;
                }

                acc.FuturesDailyLossUsedFrozen += loss;
                _logger.LogInformation("[FifoLedger] FuturesDailyLossUsed +{Loss:N0} => {Total:N0} tradeDate={TradeDate}",
                    loss, acc.FuturesDailyLossUsedFrozen, tradeDate.ToString("yyyy-MM-dd"));
                return;
            }

            if (acc.StockDailyLossTradeDate != tradeDate)
            {
                _logger.LogWarning("[TradeDate][Mismatch] component=Ledger mkt=Stock tradeDate={TradeDate} frozenDate={FrozenDate} sourceLedger={Source}",
                    tradeDate.ToString("yyyy-MM-dd"), acc.StockDailyLossTradeDate.ToString("yyyy-MM-dd"), sourceLedger);
                return;
            }

            acc.StockDailyLossUsedFrozen += loss;
            _logger.LogInformation("[FifoLedger] StockDailyLossUsed +{Loss:N0} => {Total:N0} tradeDate={TradeDate}",
                loss, acc.StockDailyLossUsedFrozen, tradeDate.ToString("yyyy-MM-dd"));
        }

        private void UpdateRoundAccumulator(
            string accountKey,
            string symbol,
            string account,
            DateTime exitTime,
            string exitFillId,
            RealizedLeg leg)
        {
            var key = (Account: accountKey, Symbol: symbol);
            if (!_rounds.TryGetValue(key, out var round))
            {
                round = new TradeRound
                {
                    AccountKey = accountKey,
                    Account = string.IsNullOrWhiteSpace(account) ? accountKey : account,
                    Symbol = symbol,
                    EntryTime = DateTime.MaxValue
                };
                _rounds[key] = round;
            }

            round.ExitTime = exitTime;
            round.ExitFillId = exitFillId;
            round.LegIds.Add(leg.LegId);
            round.GrossPnL += leg.GrossPnL;
            round.Fee += leg.Fee;
            round.Tax += leg.Tax;
            round.TotalQty += leg.CloseQty;

            if (!IsUnknownStrategy(leg.StrategyId))
            {
                if (IsUnknownStrategy(round.StrategyId))
                    round.StrategyId = leg.StrategyId;
                else if (!string.Equals(round.StrategyId, leg.StrategyId, StringComparison.OrdinalIgnoreCase))
                    round.AttributionConflict = true;
            }
            else if (string.IsNullOrWhiteSpace(round.StrategyId))
            {
                round.StrategyId = UnknownStrategyId;
            }

            if (leg.AttributionConflict || string.Equals(leg.StrategyId, MixedStrategyId, StringComparison.OrdinalIgnoreCase))
                round.AttributionConflict = true;

            if (round.AttributionConflict)
                round.StrategyId = MixedStrategyId;

            foreach (var lot in leg.MatchedLots)
            {
                if (lot.EntryTime < round.EntryTime)
                {
                    round.EntryTime = lot.EntryTime;
                    round.EntryFillId = string.IsNullOrWhiteSpace(round.EntryFillId) ? lot.SourceFillId : round.EntryFillId;
                }

                round.EntryPriceValue += lot.EntryPrice * lot.MatchedQty;
                round.ExitPriceValue += leg.ExitPrice * lot.MatchedQty;
            }

            if (round.EntrySide == null)
                round.EntrySide = leg.ExitSide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        }

        private bool TryFinalizeRound(string accountKey, string symbol, out CompletedTrade? trade)
        {
            trade = null;
            var key = (Account: accountKey, Symbol: symbol);
            if (!_rounds.TryGetValue(key, out var round)) return false;

            if (round.TotalQty <= 0)
            {
                _rounds.Remove(key);
                return false;
            }

            var avgEntry = round.EntryPriceValue / round.TotalQty;
            var avgExit = round.ExitPriceValue / round.TotalQty;
            var netPnL = round.GrossPnL - round.Fee - round.Tax;
            var holdSeconds = round.EntryTime == DateTime.MaxValue
                ? 0
                : (long)(round.ExitTime - round.EntryTime).TotalSeconds;
            var strategyId = round.AttributionConflict ? MixedStrategyId : round.StrategyId;

            trade = new CompletedTrade
            {
                StockId = symbol,
                EntryPrice = avgEntry,
                ExitPrice = avgExit,
                EntryTime = round.EntryTime == DateTime.MaxValue ? round.ExitTime : round.EntryTime,
                ExitTime = round.ExitTime,
                Commission = round.Fee,
                Tax = round.Tax,
                GrossPnL = round.GrossPnL,
                NetPnL = netPnL,
                Direction = round.EntrySide ?? OrderSide.Buy,
                Quantity = round.TotalQty,
                Account = round.Account,
                AccountKey = round.AccountKey,
                StrategyId = strategyId,
                EntryCorrelationId = round.EntryFillId,
                ExitCorrelationId = round.ExitFillId,
                AttributionConflict = round.AttributionConflict,
                HoldDurationSeconds = holdSeconds,
                LegIds = round.LegIds.ToList()
            };

            _rounds.Remove(key);
            return true;
        }

        private void AppendFillHistory(string accountKey, FillHistoryRecord record)
        {
            try
            {
                var path = GetFillHistoryPath(accountKey);
                EnsureDir(path);
                var json = JsonSerializer.Serialize(record, _jsonlOptions);
                lock (_fileLock)
                {
                    File.AppendAllText(path, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to append fill history.");
            }
        }

        private void LoadTodayProcessedFills()
        {
            try
            {
                // To support multi-account we check the keys we just loaded lots for.
                var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_isSimMode)
                {
                    var sim = CapitalConfigService.LoadAllSimAccounts();
                    foreach (var a in sim) accountKeys.Add(a.Id);
                    accountKeys.Add("Sim:Paper:All:Default");
                }
                else
                {
                    accountKeys.Add("Real");
                }

                lock (_sync)
                {
                    _processedFills.Clear();
                    
                    foreach (var accountKey in accountKeys)
                    {
                        var path = GetFillHistoryPath(accountKey);
                        if (!File.Exists(path)) continue;

                        // Only read the file if it was modified today (meaning it contains today's trades)
                        var lastWrite = File.GetLastWriteTime(path);
                        if (lastWrite.Date != _clock.Now.Date) continue;

                        var lines = File.ReadAllLines(path);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                var record = JsonSerializer.Deserialize<FillHistoryRecord>(line, _jsonlOptions);
                                if (record != null)
                                {
                                    // Make sure it matches the signature format in OnOrderFill
                                    var sig = $"{record.Symbol}_{record.Side}_{record.Qty}_{record.Price}_{record.Time.Ticks}_{record.OrderId}";
                                    _processedFills.Add(sig);
                                }
                            }
                            catch
                            {
                                // Ignore malformed lines from legacy crashes
                            }
                        }
                    }
                }
                _logger.LogInformation("[FifoLedger] Loaded {Count} historical fill signatures for deduplication.", _processedFills.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to load historical fill signatures.");
            }
        }

        private void AppendLeg(string accountKey, RealizedLeg leg)
        {
            try
            {
                var path = GetLegsPath(accountKey);
                EnsureDir(path);
                var json = JsonSerializer.Serialize(leg, _jsonlOptions);
                lock (_fileLock)
                {
                    File.AppendAllText(path, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to append leg history.");
            }
        }

        private void MarkOpenLotsDirty(string accountKey)
        {
            _openLotsDirty = true;
            _dirtyAccounts.Add(accountKey);
            _flushTimer.Stop();
            _flushTimer.Start();
        }

        private void FlushOpenLots()
        {
            List<string> accounts;
            Dictionary<string, OpenLotsState> snapshots = new(StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
                if (!_openLotsDirty) return;
                _openLotsDirty = false;

                accounts = _dirtyAccounts.ToList();
                _dirtyAccounts.Clear();

                foreach (var account in accounts)
                {
                    var state = new OpenLotsState
                    {
                        SchemaVersion = 1,
                        AccountKey = account,
                        UpdatedAt = _clock.Now,
                        Lots = new List<SymbolLots>()
                    };

                    if (_openLots.TryGetValue(account, out var symbols))
                    {
                        foreach (var kvp in symbols)
                        {
                            state.Lots.Add(new SymbolLots
                            {
                                Symbol = kvp.Key,
                                Lots = kvp.Value.ToList()
                            });
                        }
                    }

                    snapshots[account] = state;
                }
            }

            foreach (var kvp in snapshots)
            {
                try
                {
                    var path = GetOpenLotsPath(kvp.Key);
                    EnsureDir(path);
                    var json = JsonSerializer.Serialize(kvp.Value, _jsonOptions);
                    File.WriteAllText(path, json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FifoLedger] Failed to flush open lots for {Account}", kvp.Key);
                }
            }
        }

        private void LoadOpenLotsForMode()
        {
            var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (_isSimMode)
                {
                    var sim = CapitalConfigService.LoadAllSimAccounts();
                    foreach (var a in sim) accountKeys.Add(a.Id);
                    accountKeys.Add("Sim:Paper:All:Default");
                }
                else
                {
                    accountKeys.Add("Real");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to resolve account list, fallback to defaults.");
                accountKeys.Add(_isSimMode ? "Sim:Paper:All:Default" : "Real");
            }

            foreach (var account in accountKeys)
            {
                LoadOpenLots(account);
            }
        }

        private void LoadOpenLots(string accountKey)
        {
            var path = GetOpenLotsPath(accountKey);
            if (!File.Exists(path))
            {
                var legacy = GetLegacyOpenLotsPath(accountKey);
                if (File.Exists(legacy))
                    path = legacy;
                else
                    return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<OpenLotsState>(json, _jsonOptions);
                if (state?.Lots == null) return;

                var symbols = new Dictionary<string, Queue<OpenLot>>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in state.Lots)
                {
                    if (string.IsNullOrWhiteSpace(entry.Symbol)) continue;

                    var queue = new Queue<OpenLot>();
                    foreach (var lot in entry.Lots ?? new List<OpenLot>())
                    {
                        if (string.IsNullOrWhiteSpace(lot.LotId))
                            lot.LotId = string.IsNullOrWhiteSpace(lot.SourceFillId) ? Guid.NewGuid().ToString("N") : lot.SourceFillId;
                        if (string.IsNullOrWhiteSpace(lot.SourceFillId))
                            lot.SourceFillId = lot.LotId;

                        queue.Enqueue(lot);
                    }

                    if (queue.Count > 0)
                        symbols[entry.Symbol] = queue;
                }

                lock (_sync)
                {
                    _openLots[accountKey] = symbols;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FifoLedger] Failed to load open lots: {Path}", path);
            }
        }

        private (Dictionary<string, Queue<OpenLot>> Map, Queue<OpenLot> Queue) GetOrCreateSymbolLots(string accountKey, string symbol)
        {
            if (!_openLots.TryGetValue(accountKey, out var map))
            {
                map = new Dictionary<string, Queue<OpenLot>>(StringComparer.OrdinalIgnoreCase);
                _openLots[accountKey] = map;
            }

            if (!map.TryGetValue(symbol, out var queue))
            {
                queue = new Queue<OpenLot>();
                map[symbol] = queue;
            }

            return (map, queue);
        }

        private static bool IsSameSide(long lotQty, OrderSide fillSide)
        {
            return lotQty == 0 || (lotQty > 0 && fillSide == OrderSide.Buy) || (lotQty < 0 && fillSide == OrderSide.Sell);
        }

        private static bool IsUnknownStrategy(string? strategyId)
        {
            return string.IsNullOrWhiteSpace(strategyId)
                || strategyId.Equals(UnknownStrategyId, StringComparison.OrdinalIgnoreCase);
        }

        private static (string StrategyId, bool Conflict) ResolveAttribution(IEnumerable<MatchedLot> matchedLots)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lot in matchedLots)
            {
                if (!string.IsNullOrWhiteSpace(lot.EntryStrategyId))
                    ids.Add(lot.EntryStrategyId.Trim());
            }

            if (ids.Count == 0)
                return (UnknownStrategyId, false);

            if (ids.Count == 1)
                return (ids.First(), false);

            return (MixedStrategyId, true);
        }

        private static string ResolveAccountKey(OrderFillEvent fill)
        {
            if (!string.IsNullOrWhiteSpace(fill.AccountKey)) return fill.AccountKey;

            var resolved = CapitalConfigService.ResolveAccountKey(fill.Account);
            if (!string.IsNullOrWhiteSpace(resolved) && !resolved.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return resolved;

            return string.IsNullOrWhiteSpace(fill.Account) ? "Real" : fill.Account;
        }

        private MarketKind ResolveMarketKind(string symbol)
        {
            if (_marketInfoProvider.TryGetMarketInfo(symbol, out var info) && info.Kind != MarketKind.Unknown)
                return info.Kind;

            if (_tradeRulesProvider.Current.Futures.ContainsKey(symbol))
                return MarketKind.Derivatives;

            return MarketKind.Securities;
        }

        private decimal ResolveMultiplier(string symbol, MarketKind marketKind)
        {
            var f = ResolveFutureSpec(symbol, _tradeRulesProvider.Current);
            if (f != null)
                return f.PointValue;

            return marketKind == MarketKind.Derivatives ? 1m : 1000m;
        }

        private decimal EstimateFillFee(string symbol, decimal price, long qty, MarketKind marketKind)
        {
            if (qty <= 0) return 0m;
            var rules = _tradeRulesProvider.Current;

            if (marketKind == MarketKind.Derivatives)
            {
                var f = ResolveFutureSpec(symbol, rules);
                if (f != null)
                {
                    // 修正：手續費為雙邊 (round-trip)，因此單邊需除以 2
                    return (f.FeePerOrder / 2m) * Math.Abs(qty);
                }
                return 25m * Math.Abs(qty); // Default
            }

            var value = price * Math.Abs(qty) * 1000m;
            var fee = Math.Floor(value * 0.001425m * (rules.Stock?.FeeDiscount ?? 1m));
            return Math.Max(20m, fee); // Added Min fee 20
        }

        private decimal EstimateFillTax(string symbol, decimal price, long qty, OrderSide side, MarketKind marketKind)
        {
            if (qty <= 0) return 0m;
            var rules = _tradeRulesProvider.Current;

            if (marketKind == MarketKind.Derivatives)
            {
                var f = ResolveFutureSpec(symbol, rules);
                if (f != null)
                {
                    var value = price * Math.Abs(qty) * f.PointValue;
                    return Math.Floor(value * (f.TaxRate > 0 ? f.TaxRate : 0.00002m));
                }
                return 0m;
            }

            if (side != OrderSide.Sell) return 0m;

            var stockValue = price * Math.Abs(qty) * 1000m;
            return Math.Floor(stockValue * (rules.Stock?.TaxRate ?? 0.003m));
        }

        private decimal EstimateExitTax(string symbol, decimal price, long qty, DateTime entryTime, DateTime exitTime, MarketKind marketKind)
        {
            if (qty <= 0) return 0m;
            var rules = _tradeRulesProvider.Current;

            if (marketKind == MarketKind.Derivatives)
            {
                var f = ResolveFutureSpec(symbol, rules);
                if (f != null)
                {
                    var value = price * Math.Abs(qty) * f.PointValue;
                    return Math.Floor(value * (f.TaxRate > 0 ? f.TaxRate : 0.00002m));
                }
                return 0m;
            }

            var stockValue = price * Math.Abs(qty) * 1000m;
            var isDayTrade = entryTime.Date == exitTime.Date;
            var taxRate = isDayTrade ? (rules.Stock?.DayTradeTaxRate ?? 0.0015m) : (rules.Stock?.TaxRate ?? 0.003m);
            return Math.Floor(stockValue * taxRate);
        }

        private MuNTrade.Abstractions.Domain.Capital.FutureContractSpec? ResolveFutureSpec(string symbol, MuNTrade.Abstractions.Domain.Capital.TradeRulesConfig rules)
        {
            if (rules.Futures == null) return null;
            if (rules.Futures.TryGetValue(symbol, out var spec)) return spec;
            if (symbol.StartsWith("MTX") || symbol.StartsWith("MX"))
            {
                if (rules.Futures.TryGetValue("MTX00", out var mtx)) return mtx;
            }
            if (symbol.StartsWith("TX") && !symbol.StartsWith("TXF"))
            {
                if (rules.Futures.TryGetValue("TX00", out var tx)) return tx;
            }
            if (symbol.StartsWith("TM"))
            {
                if (rules.Futures.TryGetValue("TM00", out var tm)) return tm;
            }
            return rules.Futures.Values.FirstOrDefault(v => symbol.Contains(v.Name));
        }

        private string GetFillHistoryPath(string accountKey)
        {
            var historyDir = HistoryPath.ResolveHistoryRoot(accountKey);
            var safeAccount = HistoryPath.SanitizeFileName(accountKey);
            return Path.Combine(historyDir, $"fills_{safeAccount}.jsonl");
        }

        private string GetOpenLotsPath(string accountKey)
        {
            var historyDir = HistoryPath.ResolveHistoryRoot(accountKey);
            var safeAccount = HistoryPath.SanitizeFileName(accountKey);
            return Path.Combine(historyDir, $"openlots_{safeAccount}.json");
        }

        private string GetLegacyOpenLotsPath(string accountKey)
        {
            var historyDir = HistoryPath.ResolveLegacyHistoryRoot(accountKey, ensureExists: false);
            var safeAccount = HistoryPath.SanitizeFileName(accountKey);
            return Path.Combine(historyDir, $"openlots_{safeAccount}.json");
        }

        private string GetLegsPath(string accountKey)
        {
            var historyDir = HistoryPath.ResolveHistoryRoot(accountKey);
            var safeAccount = HistoryPath.SanitizeFileName(accountKey);
            return Path.Combine(historyDir, $"legs_{safeAccount}.jsonl");
        }

        private static void EnsureDir(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            FlushOpenLots();
            _flushTimer?.Dispose();
        }

        private sealed class FillHistoryRecord
        {
            public string FillId { get; init; } = string.Empty;
            public string OrderId { get; init; } = string.Empty;
            public DateTime Time { get; init; }
            public string AccountKey { get; init; } = string.Empty;
            public string Symbol { get; init; } = string.Empty;
            public OrderSide Side { get; init; }
            public long Qty { get; init; }
            public decimal Price { get; init; }
            public decimal Fee { get; init; }
            public decimal Tax { get; init; }
            public MarketKind MarketKind { get; init; }
        }

        private sealed class TradeRound
        {
            public string AccountKey { get; init; } = string.Empty;
            public string Account { get; init; } = string.Empty;
            public string Symbol { get; init; } = string.Empty;
            public OrderSide? EntrySide { get; set; }
            public DateTime EntryTime { get; set; }
            public DateTime ExitTime { get; set; }
            public decimal EntryPriceValue { get; set; }
            public decimal ExitPriceValue { get; set; }
            public long TotalQty { get; set; }
            public decimal GrossPnL { get; set; }
            public decimal Fee { get; set; }
            public decimal Tax { get; set; }
            public string StrategyId { get; set; } = string.Empty;
            public bool AttributionConflict { get; set; }
            public string EntryFillId { get; set; } = string.Empty;
            public string ExitFillId { get; set; } = string.Empty;
            public List<string> LegIds { get; } = new();
        }
    }
}

