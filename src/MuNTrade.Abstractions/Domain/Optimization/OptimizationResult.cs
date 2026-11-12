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
        // --- 識別與績效 ---
        public string JobId { get; set; }
        public double NetPnL { get; set; }   // 原本的淨利
        public double GrossPnL { get; set; }   // 新增：毛利
        public double Sharpe { get; set; }
        public int TotalTrades { get; set; } // 新增：總交易次數
        public double WinRate { get; set; }

        // ★ 新增：用於記錄該次 Job 所使用的策略參數 ★
        public double D1 { get; set; }
        public int N1 { get; set; }
        public int SL { get; set; }
        public int TP { get; set; }
        // ★ 2. 新增 TradingMode 屬性
        public TradingMode TradingMode { get; set; }

        public OptimizationResult(OptimizationJob job, BacktestPerformance performance)
        {
            JobId = job.JobId;
            NetPnL = (double)performance.NetPnL;
            GrossPnL = (double)performance.GrossPnL; // ★★★ 就是加上這一行 ★★★
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

            // ★ 3. 新增對 TradingMode 的解析
            if (job.StrategyParamOverrides.TryGetValue(nameof(StrategyParameters.TradingMode), out var modeVal))
                TradingMode = (TradingMode)modeVal;
        }
    }
}

