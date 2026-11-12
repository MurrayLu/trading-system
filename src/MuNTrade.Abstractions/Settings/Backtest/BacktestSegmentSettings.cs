using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Settings.Backtest
{
    // 這個 record 對應 JSON 陣列中的單一物件
    public record BacktestSegmentConfig
    {
        public string Symbol { get; init; } = string.Empty;
        public DateOnly Date { get; init; }
        public TimeOnly From { get; init; }
        public TimeOnly To { get; init; }
    }

    // 這個類別對應整個 "BacktestSegments" 區段
}
