// 檔案路徑: MuNTrade.Core/Services/EventWiringService.cs (新增檔案)

using Microsoft.Extensions.Hosting;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Core.Trading;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 一個背景服務，其唯一目的是在應用程式啟動時，
    /// 透過依賴注入「喚醒」所有需要監聽事件的核心服務，
    /// 確保它們的建構式被執行，事件訂閱被正確建立。
    /// </summary>
    public class EventWiringService : IHostedService
    {
        // 透過在建構式中注入這些服務，DI 容器將被迫建立它們的實例。
        public EventWiringService(
            // IStrategyEngine strategyEngine,
            IPositionManagerService positionManager,
            IRiskManagerService riskManager,
            IOrderExecutionService orderExecutionService)
        {
            // 建構式為空，注入本身就是目的。
        }

        // 我們不需要在這個服務中執行任何長時間運行的任務。
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
