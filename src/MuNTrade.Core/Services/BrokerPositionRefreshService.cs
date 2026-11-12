using Microsoft.Extensions.Hosting;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Contracts.Brokers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Services
{
    public sealed class BrokerPositionRefreshService : BackgroundService
    {
        private readonly ICapitalApiService _api;

        public BrokerPositionRefreshService(ICapitalApiService api) => _api = api;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // [Disabled] RefreshService is disabled in favor of BrokerPositionSyncService (Event-Driven).
            // This prevents uncontrolled periodic polling.
            // If periodic sync is needed in future, use BrokerPositionSyncService.RequestFullSync("Periodic");
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken); // Sleep forever-ish
            }
        }
    }
}

