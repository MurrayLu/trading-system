using Microsoft.Extensions.DependencyInjection;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Trading;
using System;

namespace MuNTrade.Core.Trading.Adapters
{
    public class PositionBridge
    {
        private readonly IEventBus _bus;

        private readonly IPositionManagerService? _oldPositionManager;

        /// <summary>
        /// 建構子不再直接注入 IPositionManagerService，而是注入 IServiceProvider。
        /// </summary>
        /// <param name="bus">事件匯流排</param>
        /// <param name="serviceProvider">服務提供者，用於可選地解析服務</param>
        public PositionBridge(IEventBus bus, IServiceProvider serviceProvider)
        {
            _bus = bus;

            // 如果 IPositionManagerService 在當前模式下沒有被註冊，GetService 會返回 null，
            // 而不會像 GetRequiredService 那樣拋出例外。
            _oldPositionManager = serviceProvider.GetService<IPositionManagerService>();

            // 事件訂閱邏輯保持不變
            _bus.Subscribe<OrderFillEvent>(OnFill);
        }

        private void OnFill(OrderFillEvent fill)
        {
            // 這裡再主動呼叫 UpdatePositionOnFill 會導致部位雙倍計算 (下 2 張變 4 張的 Bug)。
            // 因此將這行註解掉，避免重複派送成交事件。

        }
    }
}
