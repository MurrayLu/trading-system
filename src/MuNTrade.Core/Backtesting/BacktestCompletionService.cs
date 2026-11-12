
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Core.Services;

namespace MuNTrade.Core.Backtesting
{
    public class BacktestCompletionService
    {
        private readonly TradeLoggerService _tradeLogger;

        public BacktestCompletionService(TradeLoggerService tradeLogger)
        {
            _tradeLogger = tradeLogger;
        }

        // Records a fill received from the backtest engine.
        public void RecordFill(OrderFillEvent fill)
        {
            _tradeLogger.RecordFill(fill);
        }

        // Calculates performance from the recorded trades.
        public BacktestPerformance CalculatePerformance()
        {
            return _tradeLogger.CalculatePerformance();
        }
    }
}

