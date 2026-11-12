//（暫存 Intent → 回報配對）

using System.Collections.Concurrent;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;

namespace MuNTrade.Core.Trading.Adapters
{
    public interface IExecutionTraceRegistry
    {
        void Put(ExecutionTrace trace);
        bool TryGet(string correlationId, out ExecutionTrace trace);
        void Remove(string correlationId);
    }

    public sealed class ExecutionTraceRegistry : IExecutionTraceRegistry
    {
        private readonly ConcurrentDictionary<string, ExecutionTrace> _map = new();
        public void Put(ExecutionTrace trace) => _map[trace.CorrelationId] = trace;
        public bool TryGet(string cid, out ExecutionTrace trace) => _map.TryGetValue(cid, out trace!);
        public void Remove(string cid) => _map.TryRemove(cid, out _);
    }
}

