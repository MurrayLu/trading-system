// 建議路徑: MuNTrade.Abstractions/Interfaces/Hubs/IEventBus.cs
using System;

namespace MuNTrade.Abstractions.Contracts.Hubs
{
    /// <summary>
    /// 定義一個全域事件匯流排的契約，用於模組間的解耦通訊。
    /// </summary>
    public interface IEventBus
    {
        /// <summary>
        /// 向匯流排發布一個事件。
        /// </summary>
        /// <typeparam name="TEvent">事件的類型</typeparam>
        /// <param name="event">要發布的事件物件</param>
        void Publish<TEvent>(TEvent @event) where TEvent : class;

        /// <summary>
        /// 訂閱特定類型的事件。
        /// </summary>
        /// <typeparam name="TEvent">要訂閱的事件類型</typeparam>
        /// <param name="handler">處理事件的委派</param>
        /// <returns>一個 IDisposable 物件，用於取消訂閱</returns>
        IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
    }
}

