// 建議路徑: MuNTrade.Core/Trading/Adapters/PositionBridge.cs (最終修正版)
using Microsoft.Extensions.DependencyInjection; // ★ 核心修改：引用 DI 服務提供者
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

        // ★ 核心修改 1：將 oldPositionManager 改為可為 null (nullable)
        private readonly IPositionManagerService? _oldPositionManager;

        /// <summary>
        /// 建構子不再直接注入 IPositionManagerService，而是注入 IServiceProvider。
        /// </summary>
        /// <param name="bus">事件匯流排</param>
        /// <param name="serviceProvider">服務提供者，用於可選地解析服務</param>
        public PositionBridge(IEventBus bus, IServiceProvider serviceProvider)
        {
            _bus = bus;

            // ★ 核心修改 2：使用 GetService<T> 來「可選地」解析服務。
            // 如果 IPositionManagerService 在當前模式下沒有被註冊，GetService 會返回 null，
            // 而不會像 GetRequiredService 那樣拋出例外。
            _oldPositionManager = serviceProvider.GetService<IPositionManagerService>();

            // 事件訂閱邏輯保持不變
            _bus.Subscribe<OrderFillEvent>(OnFill);
        }

        private void OnFill(OrderFillEvent fill)
        {
            // ★ 核心修改：兩邊的 PositionManager 都已經在建構子中直接訂閱 _eventBus.Subscribe<OrderFillEvent> 了，
            // 這裡再主動呼叫 UpdatePositionOnFill 會導致部位雙倍計算 (下 2 張變 4 張的 Bug)。
            // 因此將這行註解掉，避免重複派送成交事件。
            // _oldPositionManager?.UpdatePositionOnFill(fill);

            // TODO: 未來，這裡也會更新你新的 IPositionManager (若有需要)
        }
    }
}
