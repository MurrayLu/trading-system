using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Domain.Optimization
{
    /// <summary>
    /// 代表單次優化工作的最終結果，用於匯出和分析。
    /// </summary>
    public class OptimizationResult
    {
        //識別與績效 ---
        public string JobId { get; set; } = string.Empty;
        public double NetPnL { get; set; }   // 原本的淨利
        public double GrossPnL { get; set; }
        public double Sharpe { get; set; }
        public int TotalTrades { get; set; }
        public double WinRate { get; set; }

        // Strategy parameters used by this optimization job.
        public double D1 { get; set; }
        public int N1 { get; set; }
        public int SL { get; set; }
        public int TP { get; set; }
        public TradingMode TradingMode { get; set; }

        public OptimizationResult(OptimizationJob job, BacktestPerformance performance)
        {
            JobId = job.JobId ?? string.Empty;
            NetPnL = (double)performance.NetPnL;
            GrossPnL = (double)performance.GrossPnL;
            Sharpe = performance.SharpRatio;
            TotalTrades = performance.Trades;
            WinRate = performance.WinRate;

            // 從 Job 的參數覆寫字典中解析出對應的值
            if (job.StrategyParamOverrides.TryGetValue(nameof(StrategyParameters.D1), out var d1Val))
                D1 = Convert.ToDouble(d1Val);

            if (job.StrategyParamOverrides.TryGetValue(nameof(StrategyParameters.N1), out var n1Val))
                N1 = Convert.ToInt32(n1Val);

            if (job.StrategyParamOverrides.TryGetValue(nameof(StrategyParameters.SL), out var slVal))
                SL = Convert.ToInt32(slVal);

            if (job.StrategyParamOverrides.TryGetValue(nameof(StrategyParameters.TP), out var tpVal))
                TP = Convert.ToInt32(tpVal);

            if (job.StrategyParamOverrides.TryGetValue(nameof(StrategyParameters.TradingMode), out var modeVal))
                TradingMode = (TradingMode)modeVal;
        }
    }
}

