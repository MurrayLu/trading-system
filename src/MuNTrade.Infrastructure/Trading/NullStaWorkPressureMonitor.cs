using System;
using MuNTrade.Abstractions.Contracts.Brokers;

namespace MuNTrade.Infrastructure.Trading
{
    internal sealed class NullStaWorkPressureMonitor : IStaWorkPressureMonitor
    {
        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
        }

        public IDisposable BeginKlineChunkFallback(string symbol, string timeframeKey) => NoopScope.Instance;

        public IDisposable BeginMarketPathActivity(string operation) => NoopScope.Instance;

        public void RecordKlineRequestCompletion(string operation, long elapsedMs, bool timedOut) { }

        public void RecordMarketPathTimeout(string operation) { }
    }
}

