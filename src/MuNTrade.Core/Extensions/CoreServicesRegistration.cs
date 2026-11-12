using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Domain.MarketData.Channels;
using MuNTrade.Abstractions.Contracts.Data;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Indicators;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Scheduling;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Settings.Rank;
using MuNTrade.Core.Backtesting;
using MuNTrade.Core.DataProcessing;
using MuNTrade.Core.DataProcessing.KLine;
using MuNTrade.Core.DataProcessing.Store;
using MuNTrade.Core.DataProcessing.TickStream;
using MuNTrade.Core.Hubs;
using MuNTrade.Core.Indicators;
using MuNTrade.Core.Indicators.Intraday;
using MuNTrade.Core.Indicators.Intraday.Vwap;
using MuNTrade.Core.Indicators.Intraday.Orb;
using MuNTrade.Core.Logging;
using MuNTrade.Core.Services;
using MuNTrade.Core.Services.Rank;
using MuNTrade.Core.Services.Dashboard;
using MuNTrade.Core.Services.EquityCurve;
using MuNTrade.Core.Services.Risk;
using MuNTrade.Core.Settings;
using MuNTrade.Core.OrderBook;
using MuNTrade.Core.Scheduling;
using MuNTrade.Core.Scheduling.Tasks;
using MuNTrade.Core.Time;
using MuNTrade.Core.Trading;
using MuNTrade.Core.Trading.Adapters;
using MuNTrade.Core.Operations.DataQuality;
using System.Threading.Channels;
using MuNTrade.Abstractions.Contracts.Capital;
using MuNTrade.Core.Capital;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Core.StrategyRuntime.Hosting;

namespace MuNTrade.Core.Extensions
{
    /// <summary>
    ///            為了滿足 RawTickChannel 的建構子，先註冊一支基底 Channel&lt;TickData&gt;。
    ///            其他任何地方都不要再直接注入 Channel&lt;TickData&gt;，統一用封裝類。
    /// </summary>
    public static class CoreServicesRegistration
    {
        // ───────────────────────────────────────────────────────────
        // 1. Core 共用基底（Options + 雜項單例）
        // ───────────────────────────────────────────────────────────
        /// <summary>
        /// 1. 註冊核心共用基礎服務與 Options。
        /// </summary>
        public static IServiceCollection AddMuNTradeCoreBase(
            this IServiceCollection services, IConfiguration cfg, AppMode appMode = AppMode.Execution)
        {
            bool isLive = appMode == AppMode.Execution;

            void AddService<TService, TImpl>()
                where TService : class
                where TImpl : class, TService
            {
                if (isLive) services.AddSingleton<TService, TImpl>();
                else services.AddScoped<TService, TImpl>();
            }

            void AddFactory<TService>(Func<IServiceProvider, TService> factory)
                where TService : class
            {
                if (isLive) services.AddSingleton(factory);
                else services.AddScoped(factory);
            }

            void AddSelf<TService>()
                where TService : class
            {
                if (isLive) services.AddSingleton<TService>();
                else services.AddScoped<TService>();
            }

            // Options
            services.Configure<ApplicationSettings>(cfg.GetSection("ApplicationSettings"));
            services.Configure<BacktestSettings>(cfg.GetSection("BacktestSettings"));
            services.Configure<DataSourceSettings>(cfg.GetSection("DataSourceSettings"));
            services.Configure<StrategyParameters>(cfg.GetSection("StrategySettings"));
            services.Configure<StrategySettings>(cfg.GetSection("StrategySettings"));
            services.Configure<ExporterSettings>(cfg.GetSection("ExporterSettings"));
            services.Configure<DataAggregatorSettings>(cfg.GetSection("DataAggregatorSettings"));
            services.Configure<CapitalManagerSettings>(cfg.GetSection("CapitalManagerSettings"));
            services.Configure<CapitalControlOptions>(cfg.GetSection("CapitalManager"));
            services.Configure<IndicatorSettings>(cfg.GetSection("IndicatorSettings"));
            services.Configure<OrbSettings>(cfg.GetSection("Indicators:Orb"));
            services.Configure<VwapSettings>(cfg.GetSection("Indicators:Vwap"));
            services.Configure<FactorStrategyOptions>(cfg.GetSection("FactorStrategy"));
            services.Configure<CisSignalOptions>(cfg.GetSection("CIS"));
            services.Configure<OrderBookProcessingOptions>(cfg.GetSection("OrderBook"));
            services.Configure<TickStreamOptions>(cfg.GetSection("TickStream"));
            services.Configure<TradingScheduleOptions>(cfg.GetSection("TradingSchedule"));
            services.Configure<TickRecordingScheduleOptions>(cfg.GetSection("TickRecordingSchedule"));
            services.Configure<RankBoardSettings>(cfg.GetSection("RankBoard"));

            // Bind risk rule settings.
            services.Configure<DayTradingRules>(cfg.GetSection("DayTradingRules"));

            // 基礎服務
            if (isLive) services.AddSingleton<IEventBus, InMemoryEventBus>();
            else services.AddScoped<IEventBus, InMemoryEventBus>();

            AddService<ICapitalManager, CapitalAllocationService>();
            AddService<ICapitalStateRepository, CapitalStateRepository>();
            AddService<IExecutionTraceRegistry, ExecutionTraceRegistry>();
            AddSelf<StrategyLedgerService>();
            AddService<IStrategyParamService, StrategyParamService>();

            services.AddSingleton<PlaybackControlService>();
            AddService<IKLineStore, KLineStore>();

            AddService<IIndicatorCalculator, IndicatorCalculatorService>();
            AddFactory(sp => (IndicatorCalculatorService)sp.GetRequiredService<IIndicatorCalculator>());
            AddService<ITickStreamHub, TickStreamHub>();
            if (isLive)
                services.AddHostedService<IndicatorCalculatorActivator>();

            services.AddSingleton<IPriceTierService, TwsePriceTierService>();
            AddService<IQuoteStateService, QuoteStateService>();
            AddService<ITradeRulesProvider, TradeRulesProvider>(); // Provider Pattern
            AddSelf<WantGooTurnoverRankService>();
            AddSelf<DailyTurnoverRankService>();

            // Register HUD, position, and risk services for the selected mode.
            AddService<IEquityCurveService, EquityCurveService>();
            AddService<IPositionsSnapshotProvider, PositionsSnapshotProvider>();
            AddService<IDashboardService, DashboardService>();
            AddService<IRiskGuardService, RiskGuardService>();

            // 注意：IClock（LIVE/PAPER）請由 Broker(SKCOM) 的註冊擴充提供 SmoothBrokerClock
            // 回測模式的 IClock（SimClock）在 AddBacktestingServices 內註冊，會覆蓋解析
            AddService<IOrbTracker, OrbTracker>();
            AddService<IVwapTracker, VwapTracker>();
            AddService<IOrbVwapReader, OrbVwapAggregator>();

            return services;
        }

        // ───────────────────────────────────────────────────────────
        // 2. Tick 通道與過濾器（唯一真相）
        // ───────────────────────────────────────────────────────────
        /// <summary>
        ///  2. 註冊 Tick 總線與過濾服務。
        ///    這個方法會根據模式決定 SessionFilter 的生命週期。
        /// </summary>
        public static IServiceCollection AddTickPipeline(this IServiceCollection svcs, AppMode appMode)
        {
            // RawTickChannel 永遠是 Singleton，作為所有資料的唯一入口
            svcs.AddSingleton<RawTickChannel>();

            // 判斷是否為 Live 模式 (Execution)
            bool isLive = appMode == AppMode.Execution;

            if (isLive)
            {
                // SessionFilter 作為背景服務持續運行
                svcs.AddHostedService<SessionFilterService>();
            }
            else
            {
                // 批次模式下，BacktestEngine 會直接使用 Scoped 的 SessionFilterService
                svcs.AddScoped<SessionFilterService>();
                // 批次模式不需要全域的 TickDispatcher，每個回測都在自己的 Scope 內處理
                svcs.AddScoped<FilteredTickChannel>();
            }

            return svcs;
        }

        // ───────────────────────────────────────────────────────────
        // 3. KLine pipeline
        // ───────────────────────────────────────────────────────────
        /// <summary>
        ///  3. 註冊 KLine 產生管線。
        /// </summary>
        public static IServiceCollection AddKLinePipeline(this IServiceCollection svcs, AppMode appMode)
        {
            bool isLive = appMode == AppMode.Execution;

            if (isLive)
            {
                svcs.AddSingleton<IKLineHub, KLineHub>();
                svcs.AddSingleton<IKLineBuilder, KLineBuilder>();
                svcs.AddHostedService<KLineBuilderService>();
            }
            else
            {
                svcs.AddScoped<IKLineHub, KLineHub>();
                svcs.AddScoped<IKLineBuilder, KLineBuilder>();
                svcs.AddScoped<KLineBuilderService>();
            }
            return svcs;
        }

        // ───────────────────────────────────────────────────────────
        // 5. Backtesting pipeline
        // ───────────────────────────────────────────────────────────
        /// <summary>
        ///  5. 註冊回測/優化專用的核心服務。
        ///    這些服務只在批次模式下使用，因此全部註冊為 Scoped。
        /// </summary>
        public static IServiceCollection AddBacktestingServices(this IServiceCollection svcs)
        {
            // 回測專用時鐘（覆蓋解析 IClock）
            svcs.AddScoped<IClock, SimClock>();

            // Backtest core services
            svcs.AddScoped<ISubscriptionManager, BacktestSubscriptionManager>();
            svcs.AddScoped<BacktestPriceCache>();
            svcs.AddScoped<ITradeExecutionManager, BacktestTradeExecutionManager>();
            svcs.AddScoped<StrategyIntentExecutionAdapter>();
            svcs.AddScoped<IPositionManagerService, PositionManagerService>();
            svcs.AddScoped<IRiskManagerService, RiskManagerService>();
            svcs.AddScoped<IOrderExecutionService, BacktestOrderExecutionService>();
            svcs.AddScoped<BacktestRiskCounterService>();
            svcs.AddScoped<IBacktestRiskGate, BacktestRiskGateService>();
            svcs.AddScoped<MuNTrade.Core.Services.Capital.AccountLedgerResolver>();
            svcs.AddScoped<BacktestDailyLossFreezer>();
            svcs.AddScoped<BacktestOrderGateway>(sp => (BacktestOrderGateway)sp.GetRequiredService<IOrderGateway>());

            // Strategy runtime (Backtest)
            svcs.AddAllStrategyTemplates();
            svcs.AddScoped<MuNTrade.Abstractions.Contracts.Strategy.IStrategyRegistry, MuNTrade.Core.StrategyRuntime.Hosting.StrategyRegistry>();
            svcs.AddScoped<MuNTrade.Core.StrategyRuntime.Routing.MarketEventRouter>();

            // Universe Planning (Phase A)
            svcs.AddScoped<MuNTrade.Core.Backtesting.Universe.IBacktestUniversePlanner, MuNTrade.Core.Backtesting.Universe.BacktestUniversePlanner>();

            // Bounded Tail Cache (Phase C)
            svcs.AddScoped<MuNTrade.Core.Backtesting.Warmup.IKLineHistoryProvider, MuNTrade.Core.Backtesting.Warmup.LocalDiskKLineProvider>();
            svcs.AddScoped<MuNTrade.Core.Backtesting.Warmup.IWarmupCacheManager, MuNTrade.Core.Backtesting.Warmup.WarmupCacheManager>();

            // Market info for backtest
            svcs.AddScoped<MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider, BacktestMarketInfoProvider>();

            // Capital provider for risk checks
            svcs.AddScoped<MuNTrade.Abstractions.Contracts.Capital.ICapitalProvider, MuNTrade.Core.Capital.SimpleCapitalProvider>();

            // Backtest Host + Reports
            svcs.AddSingleton<BacktestReportWriter>();
            svcs.AddTransient<BacktestHost>();

            // Legacy backtest engine (kept for compatibility)
            svcs.AddScoped<IBacktestEngine, BacktestEngine>();

            return svcs;
        }

        /// <summary>
        ///  livebarpipeline 合併入口
        /// </summary>
        public static IServiceCollection AddBarPipelines(this IServiceCollection svcs, AppMode appMode)
        {
            svcs.AddKLinePipeline(appMode);
            return svcs;
        }

        /// <summary>
        /// GovernedDaytradeBatch 專用資料平面註冊。
        /// 只保留 replay / warmup / builder / backtesting core，不註冊任何背景 hosted service。
        /// </summary>
        public static IServiceCollection AddGovernedDaytradeBatchDataPlane(this IServiceCollection services)
        {
            services.AddSingleton<RawTickChannel>();
            services.AddScoped<SessionFilterService>();
            services.AddScoped<FilteredTickChannel>();

            services.AddScoped<IKLineHub, KLineHub>();
            services.AddScoped<IKLineBuilder, KLineBuilder>();
            services.AddScoped<KLineBuilderService>();

            services.AddScoped<IClock, SimClock>();

            services.AddScoped<ISubscriptionManager, BacktestSubscriptionManager>();
            services.AddScoped<BacktestPriceCache>();
            services.AddScoped<ITradeExecutionManager, BacktestTradeExecutionManager>();
            services.AddScoped<StrategyIntentExecutionAdapter>();
            services.AddScoped<IPositionManagerService, PositionManagerService>();
            services.AddScoped<IRiskManagerService, RiskManagerService>();
            services.AddScoped<IOrderExecutionService, BacktestOrderExecutionService>();
            services.AddScoped<BacktestRiskCounterService>();
            services.AddScoped<IBacktestRiskGate, BacktestRiskGateService>();
            services.AddScoped<MuNTrade.Core.Services.Capital.AccountLedgerResolver>();
            services.AddScoped<BacktestDailyLossFreezer>();
            services.AddScoped<BacktestOrderGateway>(sp => (BacktestOrderGateway)sp.GetRequiredService<IOrderGateway>());

            services.AddAllStrategyTemplates();
            services.AddScoped<MuNTrade.Abstractions.Contracts.Strategy.IStrategyRegistry, MuNTrade.Core.StrategyRuntime.Hosting.StrategyRegistry>();
            services.AddScoped<MuNTrade.Core.StrategyRuntime.Routing.MarketEventRouter>();

            services.AddScoped<MuNTrade.Core.Backtesting.Universe.IBacktestUniversePlanner, MuNTrade.Core.Backtesting.Universe.BacktestUniversePlanner>();
            services.AddScoped<MuNTrade.Core.Backtesting.Warmup.IKLineHistoryProvider, MuNTrade.Core.Backtesting.Warmup.LocalDiskKLineProvider>();
            services.AddScoped<MuNTrade.Core.Backtesting.Warmup.IWarmupCacheManager, MuNTrade.Core.Backtesting.Warmup.WarmupCacheManager>();

            services.AddScoped<MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider, BacktestMarketInfoProvider>();
            services.AddScoped<MuNTrade.Abstractions.Contracts.Capital.ICapitalProvider, MuNTrade.Core.Capital.SimpleCapitalProvider>();

            services.AddSingleton<BacktestReportWriter>();
            services.AddTransient<BacktestHost>();
            services.AddScoped<IBacktestEngine, BacktestEngine>();

            return services;
        }


        public static IServiceCollection AddMarketDataPipeline(this IServiceCollection services)
        {
            // MarketDataPipeline 維持 Singleton
            services.AddSingleton<IMarketDataPipeline, MarketDataPipeline>();
            return services;
        }

        /// <summary>
        /// 註冊訂單執行相關服務 (依據模式決定 Singleton/Scoped)
        /// </summary>
        public static IServiceCollection AddOrderExecutionServices(this IServiceCollection services, AppMode appMode)
        {
            bool isLive = appMode == AppMode.Execution;

            if (isLive)
            {
                // UI / Live 模式：模擬交易狀態需要由 Singleton 保持 (Paper Account)
                services.AddSingleton<TradeLoggerService>();
                // OrderExecutionService 作為 Router，也設為 Singleton
                services.AddSingleton<IOrderExecutionService, OrderExecutionService>();
            }
            else
            {
                // 回測模式：Scoped (由 AddBacktestingServices 處理，或在此處理)
            }

            return services;
        }

        /// <summary>
        /// 註冊 live trading 所需的執行/倉位/排程基礎服務。
        /// </summary>
        public static IServiceCollection AddLiveTradingInfrastructureServices(this IServiceCollection services)
        {
            // 基礎建設
            services.AddSingleton<MarketSnapshotBuilder>();
            services.AddSingleton<MuNTrade.Core.Trading.Gateways.PaperTradingGateway>();
            services.AddSingleton<IDataHealthGuard>(sp =>
            {
                var clock = sp.GetRequiredService<IClock>();
                var logger = sp.GetRequiredService<ILogger<DataHealthGuard>>();
                var opt = sp.GetRequiredService<IOptionsMonitor<FactorStrategyOptions>>();
                return new DataHealthGuard(clock, logger, opt);
            });
            services.AddSingleton<PositionBridge>();

            // LIVE 模式的 Position Manager 和 Reader
            services.AddSingleton<LivePositionManager>();
            services.AddSingleton<IPositionManagerService>(sp => sp.GetRequiredService<LivePositionManager>());
            services.AddSingleton<IPositionReader>(sp => sp.GetRequiredService<LivePositionManager>());
            services.AddSingleton<TradeHistoryService>();
            services.AddSingleton<PositionSnapshotStore>();
            services.AddSingleton<FifoLedgerService>();
            // LIVE strategy dependencies.
            services.AddSingleton<ITradeExecutionManager, LiveOrderExecutionService>();
            services.AddSingleton<IOpportunityCostLogger, InMemoryOpportunityCostLogger>();

            services.AddSingleton<OrderBookEngine>();
            services.AddSingleton<ITradeQuoteJoiner, TradeQuoteJoiner>();
            services.AddSingleton<IOrderBookAnalytics, OrderBookAnalytics>();

            // 回報轉譯與執行橋接
            services.AddSingleton<StrategyIntentExecutionAdapter>();
            services.AddSingleton<GatewayFeedbackAdapter>();
            services.AddSingleton<BrokerReportTranslator>();
            services.AddSingleton<BrokerPositionSnapshotAdapter>();
            services.AddSingleton<ProfitLossGWReportTranslator>();

            // 喚醒服務
            services.AddHostedService<TradingSchedulerService>();
            services.AddHostedService<TradingInfrastructureActivator>();
            services.AddHostedService<BrokerPositionRefreshService>();
            services.AddHostedService<OrderBookRuntimeWiringService>();

            // 跨日靜態資料自動刷新任務
            services.AddSingleton<ITradingScheduleTask, MuNTrade.Core.Scheduling.Tasks.MorningStaticDataRefreshTask>();
            services.AddSingleton<ITradingScheduleTask, MuNTrade.Core.Scheduling.Tasks.DailyStaticDataRefreshTask>();
            services.AddSingleton<ITradingScheduleTask, MuNTrade.Core.Scheduling.Tasks.FreezeDailyLossLimitTask>();
            services.AddSingleton<ITradingScheduleTask, MuNTrade.Core.Scheduling.Tasks.TickHistoryRecordingTask>();
            services.AddSingleton<ITradingScheduleTask, MuNTrade.Core.Scheduling.Tasks.LeaveMonitorTask>();
            services.AddSingleton<ITradingScheduleTask, MuNTrade.Core.Scheduling.Tasks.EnterMonitorTask>();

            return services;
        }
    }
}

