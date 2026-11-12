using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Infrastructure.Storage.StrategyEvents
{
    public class JsonlStrategyEventStore : IStrategyEventStore
    {
        private readonly string _basePath;
        private readonly IClock _clock;

        public JsonlStrategyEventStore(IClock clock)
        {
            _clock = clock;
            _basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "strategy-events");
        }

        public async Task AppendAsync(StrategyInstanceId id, IMuEvent evt, CancellationToken ct = default)
        {
            var date = new DateTimeOffset(_clock.Now).ToString("yyyyMMdd");
            var dir = Path.Combine(_basePath, id.Value);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{date}.jsonl");

            var wrapper = new EventWrapper
            {
                Type = evt.GetType().Name,
                Payload = evt
            };

            var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions { WriteIndented = false });

            // Events are appended to the daily JSONL file.
            await File.AppendAllTextAsync(file, json + Environment.NewLine, ct);
        }

        public async IAsyncEnumerable<IMuEvent> ReadTailAsync(StrategyInstanceId id, int n, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Read the current day's file and retain the most recent requested events.
            var date = new DateTimeOffset(_clock.Now).ToString("yyyyMMdd");
            var file = Path.Combine(_basePath, id.Value, $"{date}.jsonl");

            if (!File.Exists(file)) yield break;

            var lines = await File.ReadAllLinesAsync(file, ct);
            var start = Math.Max(0, lines.Length - n);

            for (int i = start; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                IMuEvent? evt = null;
                try
                {
                    var doc = JsonDocument.Parse(lines[i]);
                    var typeName = doc.RootElement.GetProperty("Type").GetString();

                    Type? targetType = ResolveTypeInternal(typeName);
                    if (targetType != null)
                    {
                        var payload = doc.RootElement.GetProperty("Payload");
                        evt = JsonSerializer.Deserialize(payload.GetRawText(), targetType) as IMuEvent;
                    }
                }
                catch { }

                if (evt != null) yield return evt;
            }
        }

        private Type? ResolveTypeInternal(string? typeName)
        {
            if (typeName == "StrategyPacketEmittedEvent") return typeof(MuNTrade.Abstractions.Events.Strategy.StrategyPacketEmittedEvent);
            if (typeName == "StrategyStateChangedEvent") return typeof(MuNTrade.Abstractions.Events.Strategy.StrategyStateChangedEvent);
            if (typeName == "StrategyIntentEmittedEvent") return typeof(MuNTrade.Abstractions.Events.Strategy.StrategyIntentEmittedEvent);
            return null;
        }

        private class EventWrapper
        {
            public string Type { get; set; } = string.Empty;
            public object Payload { get; set; } = new();
        }
    }
}
