using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Parameterization;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Contracts.Warmup;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Backtesting;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Trading.Gateways;
using MuNTrade.Infrastructure.DataAccess.Downloaders;
using MuNTrade.Infrastructure.DataAccess.Exporters;
using MuNTrade.Infrastructure.DataAccess.FileSystem;
using MuNTrade.Infrastructure.DataAccess.Rankings;
using MuNTrade.Infrastructure.DataAccess.Selectors;
using MuNTrade.Infrastructure.DataAccess.Time;
using MuNTrade.Infrastructure.DataAccess.Warmup;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Core.Trading;
using MuNTrade.Infrastructure.Trading;
using MuNTrade.Infrastructure.Storage.Parameterization;

namespace MuNTrade.Infrastructure.Extensions
{
    public static class InfrastructureServicesRegistration
    {
        public static IServiceCollection AddGovernedDaytradeBatchInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<WarmupPlannerOptions>(configuration.GetSection("WarmupPlanner"));
            services.Configure<ApplicationSettings>(configuration.GetSection("ApplicationSettings"));

            services.AddSingleton<HistoricalDataReader>();
            services.AddSingleton<IHolidayCalendar, CsvHolidayCalendar>();
            services.AddSingleton<ITradingSessionProvider, JsonTradingSessionProvider>();
            services.AddSingleton<IResultExporter, CsvResultExporter>();
            services.AddSingleton<IHistoryWarmupService, HistoryWarmupService>();
            services.AddSingleton<IWarmupPlanner, WarmupPlanner>();
            services.AddSingleton<IDataInventoryService, JsonDataInventoryService>();
            services.TryAddSingleton<IStaWorkPressureMonitor, NullStaWorkPressureMonitor>();
            services.AddScoped<IBacktestEngine, BacktestEngine>();
            services.AddScoped<ITickSource, FileTickPlayer>();
            services.AddSingleton<ITurnoverSymbolSelector, TurnoverSymbolSelector>();
            services.AddSingleton<IStockMasterIndexProvider, JsonStockMasterIndexProvider>();
            services.AddSingleton<IWantGooTurnoverRateProvider, WantGooTurnoverRateProvider>();
            services.AddSingleton<IStockListDownloadService, StockListDownloadService>();

            services.AddSingleton<IParameterWhitelistRegistry, JsonParameterWhitelistRegistry>();
            services.AddSingleton<IParameterBlacklistRegistry, JsonParameterBlacklistRegistry>();
            services.AddSingleton<IParameterSeedRepository, JsonParameterSeedRepository>();

            services.AddSingleton<ISymbolSelector>(serviceProvider => new TurnoverSelectorAdapter(
                serviceProvider.GetRequiredService<ITurnoverSymbolSelector>(),
                serviceProvider.GetRequiredService<IClock>()));

            services.AddSingleton<IHistoricalKlineDownloader, LocalOnlyHistoricalKlineDownloader>();
            services.AddScoped<IOrderGateway, BacktestOrderGateway>();

            return services;
        }

        public static IServiceCollection AddMuNTradeInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<WarmupPlannerOptions>(configuration.GetSection("WarmupPlanner"));
            services.Configure<ApplicationSettings>(configuration.GetSection("ApplicationSettings"));

            services.AddSingleton<HistoricalDataReader>();
            services.AddSingleton<IHolidayCalendar, CsvHolidayCalendar>();
            services.AddSingleton<ITradingSessionProvider, JsonTradingSessionProvider>();
            services.AddSingleton<IResultExporter, CsvResultExporter>();
            services.AddSingleton<IHistoryWarmupService, HistoryWarmupService>();
            services.AddSingleton<IWarmupPlanner, WarmupPlanner>();
            services.AddSingleton<IDataInventoryService, JsonDataInventoryService>();
            services.TryAddSingleton<IStaWorkPressureMonitor, NullStaWorkPressureMonitor>();
            services.AddScoped<IBacktestEngine, BacktestEngine>();
            services.AddScoped<ITickSource, FileTickPlayer>();
            services.AddSingleton<ITurnoverSymbolSelector, TurnoverSymbolSelector>();
            services.AddSingleton<IStockMasterIndexProvider, JsonStockMasterIndexProvider>();
            services.AddSingleton<IWantGooTurnoverRateProvider, WantGooTurnoverRateProvider>();
            services.AddSingleton<IStockListDownloadService, StockListDownloadService>();
            services.AddSingleton<IParameterWhitelistRegistry, JsonParameterWhitelistRegistry>();
            services.AddSingleton<IParameterBlacklistRegistry, JsonParameterBlacklistRegistry>();
            services.AddSingleton<IParameterSeedRepository, JsonParameterSeedRepository>();

            //Selector 部分 (保持不變) ---
            //Selector 部分 (保持不變) ---
            services.AddSingleton<ISymbolSelector>(serviceProvider =>
            {
                // Parse AppMode
                var config = serviceProvider.GetRequiredService<IConfiguration>();
                var appModeStr = config.GetValue<string>("ApplicationSettings:AppMode");
                if (!Enum.TryParse<AppMode>(appModeStr, true, out var appMode))
                    appMode = AppMode.Execution; // Default

                return appMode switch
                {
                    AppMode.Backtest => new TurnoverSelectorAdapter(
                        serviceProvider.GetRequiredService<ITurnoverSymbolSelector>(),
                        serviceProvider.GetRequiredService<IClock>()),
                    AppMode.GovernedDaytradeBatch => new TurnoverSelectorAdapter(
                        serviceProvider.GetRequiredService<ITurnoverSymbolSelector>(),
                        serviceProvider.GetRequiredService<IClock>()),
                    // Optimization uses the configured turnover selector.
                    AppMode.Optimize => new TurnoverSelectorAdapter(
                        serviceProvider.GetRequiredService<ITurnoverSymbolSelector>(),
                        serviceProvider.GetRequiredService<IClock>()),
                    _ => new StaticListSelector(
                        serviceProvider.GetRequiredService<IOptions<BacktestSettings>>())
                };
            });

            // Register the order gateway for the selected application mode.
            var modeStr = configuration.GetValue<string>("ApplicationSettings:AppMode");
            if (!Enum.TryParse<AppMode>(modeStr, true, out var currentAppMode))
                currentAppMode = AppMode.Execution;

            switch (currentAppMode)
            {
                case AppMode.Backtest:
                case AppMode.GovernedDaytradeBatch:
                case AppMode.Optimize:
                    services.AddSingleton<IHistoricalKlineDownloader, LocalOnlyHistoricalKlineDownloader>();
                    // 在回測或最佳化模式下，使用 BacktestOrderGateway
                    // 生命週期必須與其依賴 (IPositionManagerService) 一致，改為 Scoped
                    services.AddScoped<IOrderGateway, BacktestOrderGateway>();
                    break;

                case AppMode.Execution:
                default:
                    services.AddSingleton<IHistoricalKlineDownloader, HistoricalKlineDownloader>();
                    // Execution 永遠同時支援 Paper + (可選) Live，靠 RoutingOrderGateway 分流
                    services.AddSingleton<PaperTradingGateway>();
                    services.AddSingleton<IPaperOrderGateway>(sp => sp.GetRequiredService<PaperTradingGateway>());
                    services.AddSingleton<IOrderGateway, MuNTrade.Infrastructure.Trading.Gateways.RoutingOrderGateway>();
                    services.AddSingleton<IOrderIdentityMap, OrderIdentityMap>();
                    services.AddSingleton<IOrderManagementService, OrderManagementService>();

                    // Coordinator
                    services.AddSingleton<MuNTrade.Core.Services.BrokerPositionSyncService>();

                    Console.WriteLine("▶ Execution: IOrderGateway = RoutingOrderGateway (Sim/Live by AccountKey)");
                    break;
            }

            return services;
        }
    }
}

