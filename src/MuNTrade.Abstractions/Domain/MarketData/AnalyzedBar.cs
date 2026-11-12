using System;

//分析完成的 Bar 模型

namespace MuNTrade.Abstractions.Domain.MarketData
{
    /// <summary>
    /// 包含原始 Bar 以及其對應的技術指標數據。
    /// </summary>
    public class AnalyzedBar
    {
        public DynamicBar? Bar { get; set; }
        public Dictionary<string, decimal> Indicators { get; set; } = new Dictionary<string, decimal>();
    }
}
