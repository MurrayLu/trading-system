using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Brokers; // If needed, but not used in new logic directly
using MuNTrade.Abstractions.Contracts.Capital;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Services.EquityCurve;
using System;
using System.Linq;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Market; // Added

namespace MuNTrade.Core.Capital
{
    public class CapitalAllocationService : ICapitalManager
    {
        private readonly ILogger<CapitalAllocationService> _logger;
        private readonly IOptionsMonitor<CapitalControlOptions> _options;
        private readonly IOptionsMonitor<ApplicationSettings> _appSettings;
        private readonly ICapitalStateRepository _stateRepo;
        private readonly IPriceTierService _priceTier;
        private readonly IEventBus _eventBus;
        private readonly IEquityCurveService _equity;
        private readonly IClock _clock;
        private readonly IMarketInfoProvider _marketInfo; // Injected

        public CapitalAllocationService(
            ILogger<CapitalAllocationService> logger,
            IOptionsMonitor<CapitalControlOptions> options,
            IOptionsMonitor<ApplicationSettings> appSettings,
            ICapitalStateRepository stateRepo,
            IPriceTierService priceTier,
            IEventBus eventBus,
            IEquityCurveService equity,
            IClock clock,
            IMarketInfoProvider marketInfo) // Injected
        {
            _logger = logger;
            _options = options;
            _appSettings = appSettings;
            _stateRepo = stateRepo;
            _priceTier = priceTier;
            _eventBus = eventBus;
            _equity = equity;
            _clock = clock;
            _marketInfo = marketInfo;
        }

        public async Task<int> CalculatePositionSizeAsync(string symbol, decimal entryPrice, decimal stopPrice)
        {
            // 期貨通道 (目前關閉) - [SOP-Strict] Risk Gates
            if (_marketInfo.GetMarketInfoOrThrow(symbol, MarketInfoPurpose.Risk).IsDerivatives) return 0;

            int shares = await CalcStockSize(symbol, entryPrice);
            // 回傳張數
            return shares / 1000;
        }

        public async Task<int> GetMaxPositionSizeAsync(string symbol, decimal price)
        {
            if (price <= 0)
            {
                _logger.LogDebug("[Capital] GetMaxPositionSizeAsync called with Price=0, returning fallback 100.");
                return 100; // 保留舊行為 (可能是 100 張? 需確認調用端，但依指令暫不更動此數值)
            }
            // 回傳股數 (Shares)
            return await CalcStockSize(symbol, price);
        }

        // ★★★ 核心運算：證券成長系統 ★★★
        // 回傳: 股數 (Shares)
        private async Task<int> CalcStockSize(string symbol, decimal entryPrice)
        {
            if (entryPrice <= 0) return 0;

            // 1. 讀取帳本 (目前有多少錢)
            var rules = _options.CurrentValue.StockRules;
            if (!rules.EnableAutoSizing) return 0;

            decimal equity;
            PhaseRule? phase;

            var gatewayMode = _appSettings.CurrentValue?.TradingGateway ?? "PAPER";
            if (string.Equals(gatewayMode, "LIVE", StringComparison.OrdinalIgnoreCase))
            {
                var state = await _stateRepo.LoadAsync();
                var account = state.Accounts?.FirstOrDefault(a => a.Id == state.ActiveAccountId) ?? state.Accounts?.FirstOrDefault();
                if (account == null)
                {
                    _logger.LogWarning("[Capital] No live account found. Blocking sizing.");
                    return 0;
                }

                equity = account.InitialEquity + account.RealizedPnL;
                phase = await ResolvePhaseWithGatesAsync(rules.PhaseRules, equity, state, account);
            }
            else
            {
                var sims = CapitalConfigService.LoadAllSimAccounts();
                var sim = sims.FirstOrDefault(a => a.IsActive) ?? sims.FirstOrDefault();
                if (sim == null)
                {
                    _logger.LogWarning("[Capital] No sim account found. Blocking sizing.");
                    return 0;
                }

                equity = sim.StockBalance;
                phase = ResolvePhaseWithGates(rules.PhaseRules, equity, sim);
            }

            if (phase == null)
            {
                _logger.LogWarning("[Capital] 資金 {Eq:N0} 無法對應任何階段，禁止交易 (可能低於 Phase 0 下限)。", equity);
                return 0;
            }

            // 3. [Physics Check] 判定市場物理風險 (R-Min)
            // 改用 IPriceTierService - 唯一真相來源
            decimal tickSize = _priceTier.GetTickPrice(entryPrice);

            // 公式：R-Min = (2 Tick 結構停損 + 1 Tick 滑價) = 3 Ticks
            decimal rMinPerShare = tickSize * 3;

            // 4. [Golden Number Check] 黃金數字檢核
            // 如果這檔股票太危險 (R-Min > 黃金數字)，越級打怪 -> 禁止
            if (rMinPerShare > phase.GoldenNumber)
            {
                _logger.LogWarning(
                    "[Capital] 越級打怪禁止！{Sym} R-Min({RMin}) > 黃金數字({Gold})。目前等級: {P}",
                    symbol, rMinPerShare, phase.GoldenNumber, phase.Name);
                return 0;
            }

            // 5. [Sizing] 計算可下張數
            // 本金風險金額 (1R) = 總資金 * Risk% (例如 10萬 * 1% = 1000元)
            decimal oneRAmount = equity * phase.RiskPercent;

            // 可下股數 = 1R金額 / 每股真實風險 (R-Min)
            decimal shareCount = rMinPerShare > 0 ? oneRAmount / rMinPerShare : 0;

            // 回傳股數 (轉換為 int)
            int shares = (int)shareCount;
            // 計算張數用於 Log
            int sheets = shares / 1000;

            _logger.LogInformation(
                "[Capital] {Sym} @ {Price} | Phase=[{PName}, 1R=${OneR:N0}({Pct:P1})] | " +
                "Physics=[Tick:{Tick}, R-Min:{RMin}] | Check=[{RMin}<={Gold}] | Result={Shares}股 ({Sheets}張)",
                symbol, entryPrice, phase.Name, oneRAmount, phase.RiskPercent,
                tickSize, rMinPerShare, rMinPerShare, phase.GoldenNumber, shares, sheets);

            return shares;
        }

        private void OnOrderFilled(OrderFillEvent fill)
        {
            if (fill == null || string.IsNullOrWhiteSpace(fill.StockId)) return;
            // [SOP-Strict] Use TryGet for event handling
            if (_marketInfo.TryGetMarketInfo(fill.StockId, out var info) && info.IsDerivatives) return;

            _ = UpdatePhaseStateAsync();
        }

        private void OnEquityUpdated(EquitySnapshot snapshot)
        {
            if (snapshot == null) return;
            _ = UpdatePhaseStateAsync();
        }

        private async Task UpdatePhaseStateAsync()
        {
            try
            {
                var rules = _options.CurrentValue.StockRules;
                if (!rules.EnableAutoSizing) return;

                var gatewayMode = _appSettings.CurrentValue?.TradingGateway ?? "PAPER";
                if (string.Equals(gatewayMode, "LIVE", StringComparison.OrdinalIgnoreCase))
                {
                    var state = await _stateRepo.LoadAsync();
                    var account = state.Accounts?.FirstOrDefault(a => a.Id == state.ActiveAccountId) ?? state.Accounts?.FirstOrDefault();
                    if (account == null) return;

                    var equity = account.InitialEquity + account.RealizedPnL;
                    await ResolvePhaseWithGatesAsync(rules.PhaseRules, equity, state, account);
                }
                else
                {
                    var sims = CapitalConfigService.LoadAllSimAccounts();
                    var sim = sims.FirstOrDefault(a => a.IsActive) ?? sims.FirstOrDefault();
                    if (sim == null) return;

                    ResolvePhaseWithGates(rules.PhaseRules, sim.StockBalance, sim);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Capital] Phase update failed.");
            }
        }

        private PhaseRule? ResolvePhaseWithGates(
            System.Collections.Generic.List<PhaseRule> allPhases,
            decimal currentEquity,
            SimAccountModel account)
        {
            if (allPhases == null || !allPhases.Any()) return null;

            int currentIndex = account.CurrentPhaseIndex;

            if (currentIndex < 0 || currentIndex >= allPhases.Count)
            {
                var initial = allPhases.FirstOrDefault(p => currentEquity >= p.MinEquity && currentEquity < p.MaxEquity);
                if (initial == null)
                {
                    currentIndex = currentEquity >= allPhases.Last().MaxEquity ? allPhases.Count - 1 : 0;
                }
                else
                {
                    currentIndex = allPhases.IndexOf(initial);
                }

                account.CurrentPhaseIndex = currentIndex;
                CapitalConfigService.SaveSimAccount(account, _clock);
            }

            bool changed = false;
            bool stabilityReached = false;
            while (!stabilityReached)
            {
                stabilityReached = true;
                if (currentIndex < 0 || currentIndex >= allPhases.Count) break;

                var currentPhase = allPhases[currentIndex];

                decimal upgradeThreshold = currentPhase.UpgradeAt > 0 ? currentPhase.UpgradeAt : currentPhase.MaxEquity;
                if (currentEquity >= upgradeThreshold)
                {
                    int nextIndex = currentIndex + 1;
                    if (nextIndex < allPhases.Count)
                    {
                        currentIndex = nextIndex;
                        changed = true;
                        stabilityReached = false;
                        continue;
                    }
                }

                decimal downgradeThreshold = currentPhase.DowngradeAt > 0 ? currentPhase.DowngradeAt : currentPhase.MinEquity;
                if (currentEquity < downgradeThreshold)
                {
                    int prevIndex = currentIndex - 1;
                    if (prevIndex >= 0)
                    {
                        currentIndex = prevIndex;
                        changed = true;
                        stabilityReached = false;
                        continue;
                    }
                }
            }

            if (changed)
            {
                account.CurrentPhaseIndex = currentIndex;
                _logger.LogInformation("[Capital] Phase Change Detected! New Phase: {P}. Saving state...", allPhases[currentIndex].Name);
                CapitalConfigService.SaveSimAccount(account, _clock);
            }

            return allPhases[currentIndex];
        }

        // [New] Hysteresis Logic with Persistence
        private async Task<PhaseRule?> ResolvePhaseWithGatesAsync(
            System.Collections.Generic.List<PhaseRule> allPhases,
            decimal currentEquity,
            MuNTrade.Abstractions.Domain.Capital.CapitalState state,
            MuNTrade.Abstractions.Domain.Capital.AccountProfile account)
        {
            if (allPhases == null || !allPhases.Any()) return null;

            int currentIndex = account.CurrentPhaseIndex;

            // Initial State: Find based on Min/Max (Cold Start if value is default 0 but likely valid, or invalid -1 if we supported it)
            // But 0 is "Phase 0", which is valid. 
            // Better logic: if this is first run ever? Maybe check checking consistency?
            // Hysteresis relies on trusted previous state. 
            // If the persisted state is wildly out of sync (e.g. manual edit), we might want to correct it.
            // But trusting it is safer for "Sticking" behavior. 
            // Exception: If current index is out of bounds of current rules (e.g. rules changed size), reset.
            if (currentIndex < 0 || currentIndex >= allPhases.Count)
            {
                var initial = allPhases.FirstOrDefault(p => currentEquity >= p.MinEquity && currentEquity < p.MaxEquity);
                if (initial == null)
                {
                    // Fallback
                    currentIndex = currentEquity >= allPhases.Last().MaxEquity ? allPhases.Count - 1 : 0;
                }
                else
                {
                    currentIndex = allPhases.IndexOf(initial);
                }

                // Save corrected index
                account.CurrentPhaseIndex = currentIndex;
                await _stateRepo.SaveAsync(state);
            }

            // Iterative Check for Stability (allows multi-level jumps if equity is far out)
            bool changed = false;
            bool stabilityReached = false;
            while (!stabilityReached)
            {
                stabilityReached = true; // Assume stable unless we find a transition
                if (currentIndex < 0 || currentIndex >= allPhases.Count) break; // Should be handled by initial fix, but safety

                var currentPhase = allPhases[currentIndex];

                // 1. Upgrade Check
                decimal upgradeThreshold = currentPhase.UpgradeAt > 0 ? currentPhase.UpgradeAt : currentPhase.MaxEquity;
                if (currentEquity >= upgradeThreshold)
                {
                    int nextIndex = currentIndex + 1;
                    if (nextIndex < allPhases.Count)
                    {
                        currentIndex = nextIndex;
                        changed = true;
                        stabilityReached = false; // Re-evaluate new phase rules
                        continue;
                    }
                }

                // 2. Downgrade Check
                decimal downgradeThreshold = currentPhase.DowngradeAt > 0 ? currentPhase.DowngradeAt : currentPhase.MinEquity;
                if (currentEquity < downgradeThreshold)
                {
                    int prevIndex = currentIndex - 1;
                    if (prevIndex >= 0)
                    {
                        currentIndex = prevIndex;
                        changed = true;
                        stabilityReached = false; // Re-evaluate new phase rules
                        continue;
                    }
                }
            }

            if (changed)
            {
                account.CurrentPhaseIndex = currentIndex;
                _logger.LogInformation("[Capital] Phase Change Detected! New Phase: {P}. Saving state...", allPhases[currentIndex].Name);
                await _stateRepo.SaveAsync(state);
            }

            return allPhases[currentIndex];
        }

    }
}

