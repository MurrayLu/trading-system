using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;                          // PositionDto
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;     // OrderSide
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Core.Trading;                               // LivePositionManager

namespace MuNTrade.Core.Services.Dashboard
{
    public interface IPositionsSnapshotProvider
    {
        IReadOnlyList<PositionDto> GetAll();
    }

    /// <summary>
    /// 最小可用的部位快照提供者：
    ///優先從 LivePositionManager 讀「開倉」列表（只用得到的欄位：StockId, Quantity）
    ///其它價格/成本相關欄位先置 0（之後再接真實來源）
    /// </summary>
    public sealed class PositionsSnapshotProvider : IPositionsSnapshotProvider
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<PositionsSnapshotProvider> _logger;

        public PositionsSnapshotProvider(IServiceProvider sp, ILogger<PositionsSnapshotProvider> logger)
        {
            _sp = sp;
            _logger = logger;
        }

        public IReadOnlyList<PositionDto> GetAll()
        {
            try
            {
                var live = _sp.GetService<LivePositionManager>();
                IEnumerable<Position> positions = live?.GetAllOpenPositions() ?? Enumerable.Empty<Position>();

                var list = new List<PositionDto>(capacity: 32);
                foreach (var p in positions)
                {
                    // 目前僅可信：StockId, Quantity
                    var qtyAbs = Math.Abs(p.Quantity);

                    list.Add(new PositionDto
                    {
                        Symbol = p.StockId,
                        Side = p.Quantity >= 0 ? OrderSide.Buy : OrderSide.Sell,
                        Quantity = qtyAbs,

                        // Map Account and Pricing data from Position
                        Account = p.Account,
                        AccountKey = string.IsNullOrWhiteSpace(p.AccountKey) ? p.Account : p.AccountKey,
                        AverageCost = p.AveragePrice,
                        LastPrice = p.LastPrice,
                        MarketValue = qtyAbs * p.LastPrice, // Estimate based on execution price

                        UnrealizedPnL = p.UnrealizedPnL,
                        UnrealizedPnLRatio = p.AveragePrice != 0 ? ((p.LastPrice - p.AveragePrice) / p.AveragePrice) * 100 : 0, // Keep ratio calc visualization only
                        EstimatedTotalCost = p.EstimatedTotalCost,
                        BreakEvenPrice = p.BreakEvenPrice,
                        MarketNo = p.MarketNo,
                        Source = p.Source,

                        EstimatedStopLoss = null,
                        ProfitTarget = null
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PositionsSnapshotProvider] 取得部位快照失敗，回空集合。");
                return Array.Empty<PositionDto>();
            }
        }
    }
}

