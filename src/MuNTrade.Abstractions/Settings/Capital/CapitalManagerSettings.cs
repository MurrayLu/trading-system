// 建議路徑: MuNTrade.Abstractions/Settings/CapitalManagerSettings.cs

namespace MuNTrade.Abstractions.Settings.Capital
{
    public enum SizingMode
    {
        /// <summary>
        /// 使用固定的交易數量。
        /// </summary>
        FixedQuantity,
        /// <summary>
        /// 使用固定的投入資金來計算數量。
        /// </summary>
        FixedCapital
    }

    public class CapitalManagerSettings
    {
        /// <summary>
        /// 設定部位規模的計算模式。
        /// </summary>
        public SizingMode SizingMode { get; set; } = SizingMode.FixedQuantity;

        /// <summary>
        /// 在 FixedQuantity 模式下，每個標的預設的交易數量。
        /// </summary>
        public long DefaultFixedQuantity { get; set; } = 1; // 預設 1 張/口

        /// <summary>
        // 在 FixedCapital 模式下，每個標的預設的投入資金 (台幣)。
        /// </summary>
        public decimal DefaultFixedCapital { get; set; } = 200_000m; // 預設 20 萬

        /// <summary>
        /// 針對特定商品覆寫預設設定 (可選)。
        /// 例如: "TX00": { "SizingMode": "FixedQuantity", "DefaultFixedQuantity": 2 }
        /// </summary>
        public Dictionary<string, CapitalManagerSettings> SymbolSpecific { get; set; } = new();
    }
}

