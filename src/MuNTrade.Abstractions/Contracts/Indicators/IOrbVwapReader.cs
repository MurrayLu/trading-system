using System;

namespace MuNTrade.Abstractions.Contracts.Indicators
{
    // 薄聚合器：提供「一起讀」的快照；同時暴露 OnTrade 讓 Pipeline 只呼叫一次
    public interface IOrbVwapReader
    {
        void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal);
        OrbVwapCombined? Get(string symbol);
        void Reset(string symbol, DateTime tradingDateLocal);
    }

    public sealed record OrbVwapCombined(
        DateTime TradingDate,
        decimal OrbHigh,
        decimal OrbLow,
        bool OrbClosed,
        decimal Vwap
    );
}

