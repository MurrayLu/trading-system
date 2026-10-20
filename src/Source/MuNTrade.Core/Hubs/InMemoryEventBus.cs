// 建議路徑: MuNTrade.Core/Hubs/InMemoryEventBus.cs
using MuNTrade.Abstractions.Contracts.Hubs;
using System;
using System.Collections.Concurrent;

namespace MuNTrade.Core.Hubs
{
    /// <summary>
    /// 一個基於強參考且具備例外隔離的記憶體事件匯流排。
    /// 此版本執行緒安全，且效能優於使用傳統鎖的實作。
    /// </summary>
    public sealed class InMemoryEventBus : IEventBus
    {
        // 使用巢狀的 ConcurrentDictionary 來儲存訂閱。
        // 外層 Key: 事件類型 (Type)
        // 內層 Key: 處理常式委派 (Delegate)，本身就是強參考。Value (byte) 僅作為佔位符。
        // 這個結構可以達到高效的執行緒安全，無需手動鎖定。
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Delegate, byte>> _subs = new();

        /// <summary>
        /// 訂閱一個事件。
        /// </summary>
        /// <typeparam name="TEvent">事件的類型。</typeparam>
        /// <param name="handler">事件發生時要執行的處理常式。</param>
        /// <returns>一個 IDisposable 物件，用於取消訂閱。</returns>
        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            // GetOrAdd確保了對特定事件類型的訂閱集合只會被建立一次。
            var bag = _subs.GetOrAdd(typeof(TEvent), _ => new ConcurrentDictionary<Delegate, byte>());

            // 將處理常式本身作為 Key 加入，實現強參考儲存。
            bag[handler] = 0;

            // 返回一個 Unsubscriber 物件，它的 Dispose 方法會執行退訂邏輯。
            return new Unsubscriber(() => bag.TryRemove(handler, out _));
        }

        /// <summary>
        /// 發布一個事件給所有訂閱者。
        /// </summary>
        /// <typeparam name="TEvent">事件的類型。</typeparam>
        /// <param name="event">要發布的事件物件。</param>
        public void Publish<TEvent>(TEvent @event) where TEvent : class
        {
            // 如果沒有任何處理常式訂閱此事件，則直接返回。
            if (!_subs.TryGetValue(typeof(TEvent), out var bag))
            {
                return;
            }

            // 遍歷所有訂閱者並執行它們。
            foreach (var (handlerDelegate, _) in bag)
            {
                try
                {
                    // 執行委派。
                    ((Action<TEvent>)handlerDelegate)(@event);
                }
                catch (Exception ex)
                {
                    // 例外隔離：捕獲單一訂閱者的錯誤，防止中斷整個回呼鏈。
                    // 建議在此處加入日誌紀錄，以便追蹤有問題的訂閱者。
                    // 例如: _logger.LogError(ex, "EventBus subscriber threw an exception.");
                }
            }
        }

        /// <summary>
        /// 一個輔助類別，用於處理退訂邏輯。
        /// </summary>
        private sealed class Unsubscriber : IDisposable
        {
            private readonly Action _disposeAction;

            public Unsubscriber(Action disposeAction) => _disposeAction = disposeAction;

            public void Dispose() => _disposeAction();
        }
    }
}
