// MuNTrade.Core/Services/QuoteStateService.cs
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
using MuNTrade.Abstractions.Contracts.Brokers; // ISubscriptionManager
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// IQuoteStateService 的實作。
    /// 它在應用程式生命週期中作為一個單例 (Singleton) 存在，
    /// 監聽所有靜態資料事件，並將其快取在記憶體中。
    /// 現在加入了 fallback 機制，當本地快取未命中時，會嘗試向 SubscriptionManager 查詢。
    /// </summary>
    public class QuoteStateService : IQuoteStateService
    {
        private readonly ConcurrentDictionary<string, SymbolStaticDataLoadedEvent> _states =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ISubscriptionManager _subMgr;

        public QuoteStateService(IEventBus eventBus, ISubscriptionManager subMgr)
        {
            _subMgr = subMgr;

            // Push：照舊，監聽事件來更新本地快取
            eventBus.Subscribe<SymbolStaticDataLoadedEvent>(e =>
            {
                _states[e.Symbol] = e;
            });
        }

        public bool TryGetState(string symbol, [NotNullWhen(true)] out SymbolStaticDataLoadedEvent? state)
        {
            // 1. 先查本地快取 (最快)
            if (_states.TryGetValue(symbol, out state))
                return true;

            // 2. Pull：若本地沒有，向 SubscriptionManager 的 cache 補抓
            if (_subMgr.TryGetInitialState(symbol, out var cached))
            {
                _states[cached.Symbol] = cached;
                state = cached;
                return true;
            }

            // [New] 3. Alias Fallback: 處理 TX00AM 映射回 TX00 的情境
            if (symbol.EndsWith("AM", StringComparison.OrdinalIgnoreCase))
            {
                var baseSymbol = symbol.Substring(0, symbol.Length - 2);
                if (TryGetState(baseSymbol, out state))
                {
                    return true;
                }
            }

            state = null;
            return false;
        }
    }
}
