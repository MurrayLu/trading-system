using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Core.Capital;
using System;
using System.Collections.Generic;

namespace MuNTrade.Core.Services
{
    public class TradeRulesProvider : ITradeRulesProvider
    {
        private readonly IEventBus _bus;
        private readonly ILogger<TradeRulesProvider> _logger;

        public TradeRulesConfig Current { get; private set; } = new();
        public event Action<TradeRulesConfig> Changed;

        public TradeRulesProvider(IEventBus bus, ILogger<TradeRulesProvider> logger)
        {
            _bus = bus;
            _logger = logger;

            Reload(); // Load initially

            _bus.Subscribe<TradeRulesChangedEvent>(_ =>
            {
                _logger.LogInformation("Trade Rules Changed Event Received. Reloading...");
                Reload();
            });
        }

        public void Reload()
        {
            try
            {
                Current = CapitalConfigService.LoadTradeRules() ?? new TradeRulesConfig();
                EnsureDefaults();
                Changed?.Invoke(Current);
                _logger.LogInformation("Trade Rules Loaded Successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load trade rules.");
            }
        }

        private void EnsureDefaults()
        {
            if (Current.Futures == null) Current.Futures = new Dictionary<string, FutureContractSpec>();

            // Safety net for TX00
            if (!Current.Futures.ContainsKey("TX00"))
            {
                Current.Futures["TX00"] = new FutureContractSpec
                {
                    PointValue = 200,
                    FeePerOrder = 200,
                    Name = "大台指"
                };
            }
            // Safety net for MTX00
            if (!Current.Futures.ContainsKey("MTX00"))
            {
                Current.Futures["MTX00"] = new FutureContractSpec
                {
                    PointValue = 50,
                    FeePerOrder = 100,
                    Name = "小台指"
                };
            }
        }
    }
}

