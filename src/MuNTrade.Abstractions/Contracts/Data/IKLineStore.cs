using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Data
{
    public interface IKLineStore
    {
        void PutInitial(string symbol, string timeframe, IReadOnlyList<JsonKLineRecord> bars);
        IReadOnlyList<JsonKLineRecord> GetTail(string symbol, string timeframe, int n);
        void UpsertLast(string symbol, string timeframe, JsonKLineRecord bar);
        void MarkTfReady(string symbol, string timeframe);
        bool IsTfReady(string symbol, string timeframe);
        bool IsAllTfReady(string symbol, IEnumerable<string> tfs);
        void Clear(string symbol);
        // 取「目前開著的棒」的快照（若有）
        bool TryGetOpenBar(string symbol, BarConfig cfg, out DynamicBar? snapshot);

        // 由外界在關棒/partial 時回寫最新的 open 狀態（可在 OnBarPartial/OnBarClosed 時呼叫）
        void UpsertOpenBar(DynamicBar bar);
    }
}

