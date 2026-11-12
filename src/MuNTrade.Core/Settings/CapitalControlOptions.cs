using System.Collections.Generic;

namespace MuNTrade.Core.Settings
{
    public class CapitalControlOptions
    {
        public StockRulesOptions StockRules { get; set; } = new();
        public FuturesRulesOptions FuturesRules { get; set; } = new();
    }

    public class StockRulesOptions
    {
        public bool EnableAutoSizing { get; set; } = true;
        public List<TickRule> TickRules { get; set; } = new();
        public List<PhaseRule> PhaseRules { get; set; } = new();
    }

    public class TickRule
    {
        public decimal MinPrice { get; set; }
        public decimal MaxPrice { get; set; }
        public decimal TickSize { get; set; }
    }

    public class PhaseRule
    {
        public string Name { get; set; } = string.Empty;
        public decimal MinEquity { get; set; }
        public decimal MaxEquity { get; set; }
        public decimal RiskPercent { get; set; }
        public decimal GoldenNumber { get; set; } // 最大每股風險容忍值
        public decimal UpgradeAt { get; set; }    // 升級門檻 (Hysteresis Upper Bound)
        public decimal DowngradeAt { get; set; }  // 降級門檻 (Hysteresis Lower Bound)
    }

    public class FuturesRulesOptions
    {
        public bool Enabled { get; set; }
        public List<PhaseRule> PhaseRules { get; set; } = new();
    }
}

