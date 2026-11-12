using Microsoft.Extensions.Hosting;
using MuNTrade.Core.Trading.Adapters;
using MuNTrade.Core.Trading;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 一個背景服務，其唯一目的是在應用程式啟動時，
    /// 確保所有交易相關的 Singleton 服務都被實例化，
    /// 以便它們的建構子可以執行事件訂閱等初始化邏輯。
    /// </summary>
    public class TradingInfrastructureActivator : IHostedService
    {
        // 在建構式中注入所需被「喚醒」的服務。
        // DI 容器會確保它們被創建。
        public TradingInfrastructureActivator(
            PositionBridge positionBridge,
            MarketSnapshotBuilder snapshotBuilder,
            StrategyIntentExecutionAdapter intentExecutionAdapter,
            GatewayFeedbackAdapter feedbackAdapter,
            LivePositionManager livePositionManager,
            FifoLedgerService fifoLedgerService,
            TradeHistoryService tradeHistoryService,
            BrokerReportTranslator brokerReportTranslator,
            BrokerPositionSnapshotAdapter brokerPositionSnapshotAdapter,
            ProfitLossGWReportTranslator profitLossGWReportTranslator)
        {
            // 建構子為空即可，DI 會完成所有工作。
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 不需要執行任何長時間運行的任務，啟動即可。
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

