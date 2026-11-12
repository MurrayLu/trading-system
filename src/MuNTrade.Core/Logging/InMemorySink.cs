using Serilog.Core;
using Serilog.Events;
using System;
using System.Reactive.Subjects;

namespace MuNTrade.Core.Logging
{
    /// <summary>
    /// 一個可觀察 (IObservable) 的 Serilog Sink。
    /// 所有通過 Serilog 記錄的事件都會被推送到這個 Sink 中，
    /// 並透過一個 ReplaySubject 廣播給所有訂閱者。
    /// </summary>
    public sealed class InMemorySink : ILogEventSink, IObservable<LogEvent>
    {
        // ReplaySubject 會「重播」最近的事件給新的訂閱者，確保即使服務稍後啟動也能收到歷史日誌。
        // 這裡我們使用無參數建構子，它會重播所有歷史事件。
        private readonly ReplaySubject<LogEvent> _subject = new();

        /// <summary>
        /// Serilog 呼叫此方法來發送日誌事件。
        /// </summary>
        public void Emit(LogEvent logEvent) => _subject.OnNext(logEvent);

        /// <summary>
        /// 讓外部觀察者 (如 LogRelayService) 訂閱日誌事件流。
        /// </summary>
        public IDisposable Subscribe(IObserver<LogEvent> observer) => _subject.Subscribe(observer);
    }
}

