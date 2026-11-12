using System;

namespace MuNTrade.Core.Indicators.KLine
{
    /// <summary>單一指標序列點</summary>
    public sealed record IndicatorSeriesPoint(DateTime EndTime, decimal Value);
}

