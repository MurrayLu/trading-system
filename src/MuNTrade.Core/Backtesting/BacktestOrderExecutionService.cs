using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest 版 IOrderExecutionService：將 OrderRequest 轉交給 IOrderGateway。
    /// </summary>
    public sealed class BacktestOrderExecutionService : IOrderExecutionService
    {
        private readonly IOrderGateway _gateway;
        private readonly ILogger<BacktestOrderExecutionService> _logger;

        public BacktestOrderExecutionService(IOrderGateway gateway, ILogger<BacktestOrderExecutionService> logger)
        {
            _gateway = gateway;
            _logger = logger;
        }

        public void ExecuteOrder(OrderRequest request)
        {
            if (request == null || request.Quantity == 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (request.MarketKind == MarketKind.Derivatives)
                        await _gateway.PlaceFutureOrderAsync(request);
                    else
                        await _gateway.PlaceStockOrderAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[BacktestOrderExecution] Failed to route order.");
                }
            });
        }

        public Task RunAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}

