using System;

namespace MuNTrade.Abstractions.Contracts.Brokers
{
    public interface IStaWorkPressureMonitor
    {
        IDisposable BeginMarketPathActivity(string operation);
        void RecordMarketPathTimeout(string operation);
        void RecordKlineRequestCompletion(string operation, long elapsedMs, bool timedOut);
        IDisposable BeginKlineChunkFallback(string symbol, string timeframeKey);
    }
}

