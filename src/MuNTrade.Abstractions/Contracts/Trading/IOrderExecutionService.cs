using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    public interface IOrderExecutionService
    {
        void ExecuteOrder(OrderRequest request);
        Task RunAsync(CancellationToken stoppingToken);
    }
}

