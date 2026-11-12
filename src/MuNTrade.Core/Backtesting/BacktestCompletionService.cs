// MuNTrade.Core/Backtesting/BacktestCompletionService.cs (v7.7.3 - 最終修正版)

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

        // ★ 新增：一個公開的方法，用於接收成交紀錄 ★
        // BacktestEngine 將會呼叫這個方法
        public void RecordFill(OrderFillEvent fill)
        {
            // 將收到的 fill 事件，直接轉交給 TradeLoggerService 進行記錄
            _tradeLogger.RecordFill(fill);
        }

        // 這個方法維持不變，它仍然是最終計算績效的入口
        public BacktestPerformance CalculatePerformance()
        {
            return _tradeLogger.CalculatePerformance();
        }
    }
}

