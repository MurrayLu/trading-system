using Microsoft.Extensions.DependencyInjection;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Core.Strategies.Sandbox;
using MuNTrade.Core.Strategies.Verification;
using MuNTrade.Core.StrategyRuntime.Verification;

namespace MuNTrade.Core.StrategyRuntime.Hosting
{
    public static class StrategyTemplateRegistration
    {
        /// <summary>
        /// 集中註冊所有策略範本 (Single Source of Truth)
        /// 供 UI.Desktop 與 UI.Backtest 共同呼叫
        /// </summary>
        public static IServiceCollection AddAllStrategyTemplates(this IServiceCollection services)
        {
            // 避免 Reflection auto-scan，採用明確的手動註冊
            services.AddScoped<IStrategyTemplate, ForcedEntryExitProbeTemplate>();
            services.AddScoped<IStrategyTemplate, MaCrossProbeTemplate>();
            services.AddScoped<IStrategyTemplate, EchoStrategyTemplate>();
            services.AddScoped<IStrategyTemplate, IngressProbeTemplate>();
            services.AddScoped<IStrategyTemplate, LedgerProbeTemplate>();
            services.AddScoped<IStrategyTemplate, Tx00MaTrendProbeTemplate>();

            // 必須同時確保 StrategyRuntime 的基礎設施已註冊
            // 因 Backtest 與 Live 可能在其他地方已經各自註冊，這裡提供一個安全的 AddIfMissing 概念
            // 但此專案慣例是直接使用 AddScoped (覆寫機制)
            services.AddScoped<IStrategyRegistry, StrategyRegistry>();
            services.AddScoped<IStrategyHost, StrategyHost>();
            // MarketEventRouter 在 AddBacktestingServices 已註冊，但為了獨立完整性不強制綁定在這裡

            return services;
        }
    }
}

