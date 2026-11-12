using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Brokers
{
    /// <summary>
    /// 管理行情訂閱的介面。
    /// </summary>
    public interface ISubscriptionManager
    {
        /// <summary>
        /// 訂閱一個商品的完整行情（包含靜態資料、快照與逐筆/五檔）。
        /// 管理器會根據商品類型自動選擇合適的 API 呼叫。
        /// </summary>
        void SubscribeQuote(string symbol);

        /// <summary>
        /// 完全退訂一個商品（包含快照與逐筆/五檔）。
        /// </summary>
        bool Unsubscribe(string symbol);

        /// <summary>
        /// 僅訂閱逐筆成交＋五檔（通常由 UI 上的「監控」開關觸發）。
        /// </summary>
        void SubscribeTicks(string symbol);

        /// <summary>
        /// 訂閱逐筆成交 (Async Version)。
        /// </summary>
        Task<bool> SubscribeTicksAsync(string symbol, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// 僅取消逐筆成交＋五檔。
        /// </summary>
        void UnsubscribeTicks(string symbol);

        /// <summary>
        /// (Async Version) 僅取消逐筆成交＋五檔。
        /// </summary>
        Task<bool> UnsubscribeTicksAsync(string symbol, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// (Async Version) 完全退訂一個商品（包含快照與逐筆/五檔）。
        /// </summary>
        Task<bool> UnsubscribeAsync(string symbol, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// 主動查詢已載入的靜態資料，解決 UI 啟動時序問題。
        /// </summary>
        bool TryGetInitialState(string symbol, [NotNullWhen(true)] out SymbolStaticDataLoadedEvent? state);

        /// <summary>
        /// 取得目前所有已訂閱的代碼。
        /// </summary>
        IReadOnlyCollection<string> Current { get; }

        /// <summary>
        /// 取得目前所有已訂閱的逐筆代碼。
        /// </summary>
        IReadOnlyCollection<string> CurrentTickSubscriptions { get; }

        /// <summary>
        /// 進入逐筆訂閱獨佔模式（用於錄檔批次）。處於獨佔模式時，其他 SubscribeTicks 會被阻擋。
        /// </summary>
        IDisposable BeginExclusiveTickSubscription(string owner);

        /// <summary>
        /// (Async Version) 逐筆訂閱（獨佔模式下允許）。
        /// </summary>
        Task<bool> SubscribeTicksExclusiveAsync(string symbol, System.Threading.CancellationToken ct = default);
        /// <summary>
        /// 手動向券商刷新報價 / 逐筆訂閱，可傳入指定商品；若參數為 null 則代表刷新所有已訂閱清單。
        /// </summary>
        /// <param name="quoteSymbols">需要重新 RequestStocks 的品種，null 代表全部。</param>
        /// <param name="tickSymbols">需要重新 RequestTicks 的品種，null 代表全部。</param>
        void RefreshQuotesAndTicks(IReadOnlyCollection<string>? quoteSymbols = null, IReadOnlyCollection<string>? tickSymbols = null);

        /// <summary>
        /// 跨日後刷新靜態資料（昨收/昨量/漲跌停等），並發布至事件總線。
        /// 若未指定 symbols，則對目前所有已訂閱的品種執行。
        /// </summary>
        void RefreshStaticData(IReadOnlyCollection<string>? symbols = null, string reason = "Unknown", bool includeUnsubscribed = false);

        /// <summary>
        /// (Async Version) 跨日後刷新靜態資料（昨收/昨量/漲跌停等），並發布至事件總線。
        /// </summary>
        Task RefreshStaticDataAsync(IReadOnlyCollection<string>? symbols = null, string reason = "Unknown", bool includeUnsubscribed = false);

        /// <summary>
        /// (Async Version) 訂閱行情（包含靜態與快照）。
        /// 回傳 true 代表靜態資料取得成功且已排入訂閱；false 代表無效代碼或失敗。
        /// </summary>
        Task<bool> SubscribeQuoteAsync(string symbol, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// (Async Version) 手動刷新報價與逐筆。
        /// </summary>
        Task RefreshQuotesAndTicksAsync(IReadOnlyCollection<string>? quoteSymbols = null, IReadOnlyCollection<string>? tickSymbols = null);
    }
}

