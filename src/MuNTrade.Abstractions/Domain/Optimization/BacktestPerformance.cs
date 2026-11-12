// MuNTrade.Abstractions/Models/BacktestPerformance.cs
namespace MuNTrade.Abstractions.Domain.Optimization
{
    public class BacktestPerformance
    {
        public int Trades { get; }
        public decimal NetPnL { get; }
        public decimal GrossPnL { get; }
        public decimal MaxDrawDown { get; }
        public double SharpRatio { get; }
        public double WinRate { get; } // ★ 新增：勝率

        public BacktestPerformance(int trades, decimal netPnL, decimal grossPnL, decimal maxDrawDown, double sharpRatio, double winRate)
        {
            Trades = trades;
            NetPnL = netPnL;
            GrossPnL = grossPnL;
            MaxDrawDown = maxDrawDown;
            SharpRatio = sharpRatio;
            WinRate = winRate; // ★ 新增
        }
    }
}
