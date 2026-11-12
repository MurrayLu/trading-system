using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Writes per-run backtest event history as JSONL.
    /// </summary>
    public sealed class BacktestEventJournal : IDisposable
    {
        private readonly ConcurrentQueue<object> _events = new();
        private readonly List<IDisposable> _subs = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        public BacktestEventJournal(IEventBus bus)
        {
            _subs.Add(bus.Subscribe<StrategyPacketEmittedEvent>(e => _events.Enqueue(e)));
            _subs.Add(bus.Subscribe<OrderFillEvent>(e => _events.Enqueue(e)));
            _subs.Add(bus.Subscribe<CompletedTrade>(e => _events.Enqueue(e)));
            _subs.Add(bus.Subscribe<StrategyStateChangedEvent>(e => _events.Enqueue(e)));
        }

        public bool HasEntries => !_events.IsEmpty;

        public IReadOnlyList<string> SnapshotEventTypeNames()
        {
            return _events.Select(evt => evt.GetType().Name).ToList();
        }

        public void FlushTo(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(true));

            while (_events.TryDequeue(out var evt))
            {
                var payload = new
                {
                    Type = evt.GetType().Name,
                    Event = evt
                };
                writer.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            }
        }

        public void Dispose()
        {
            foreach (var sub in _subs)
                sub.Dispose();
            _subs.Clear();
        }
    }
}

