using System;

namespace MuNTrade.Abstractions.Contracts.Indicators
{
    public interface IOrbTracker
    {
        void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal);
        OrbSnapshot? Get(string symbol);
        void Reset(string symbol, DateTime tradingDateLocal);
    }

    public sealed record OrbSnapshot(DateTime TradingDate, decimal High, decimal Low, bool Closed);
}

