using System;

namespace MuNTrade.Abstractions.Contracts.Indicators
{
    public interface IVwapTracker
    {
        void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal);
        VwapSnapshot? Get(string symbol);
        void Reset(string symbol, DateTime tradingDateLocal);
    }

    public sealed record VwapSnapshot(DateTime TradingDate, decimal Vwap, decimal CumPV, int CumVol);

    public sealed class VwapSettings
    {
        public string Session { get; set; } = "Regular"; // 保留擴充：半日市、延長盤等
    }
}

