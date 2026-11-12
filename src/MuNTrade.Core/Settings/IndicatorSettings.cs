using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Settings
{
    /// <summary> 指標全域設定（可用 IOptions<T> 載入）。 </summary>
    public sealed class IndicatorSettings
    {
        /// <summary> 預設計算的指標清單（套用在所有 symbol/timeframe，除非覆蓋）。 </summary>
        public DefaultIndicatorSet Defaults { get; init; } = new();

        /// <summary> 針對特定 Symbol 的覆蓋設定，例如：{ "2330": { ... } } </summary>
        public Dictionary<string, DefaultIndicatorSet> SymbolOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary> 針對特定 Timeframe 的覆蓋設定，例如：{ "1m": { ... } } </summary>
        public Dictionary<string, DefaultIndicatorSet> TimeframeOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary> partial 事件的最小間隔(ms)，避免 UI/Bus 被狂刷。 </summary>
        public int PartialThrottleMs { get; init; } = 200;
    }

    public sealed class DefaultIndicatorSet
    {
        public SmaSetting Sma { get; init; } = new();     // 支援多 Period
        public AtrSetting Atr { get; init; } = new();     // Wilder ATR
        // 之後要加：EMA/RSI/BBands/Med 等都可在此擴充
    }

    public sealed class SmaSetting
    {
        public bool Enabled { get; init; } = true;
        /// <summary> 要計算的多個 SMA 週期，例如 [5, 10, 20]。 </summary>
        public int[] Periods { get; init; } = new[] { 5, 10, 20 };
        /// <summary> 啟動前需要的歷史長度，用於 warm-up。預設為最大 Period。 </summary>
        public int Warmup => Periods is { Length: > 0 } ? Math.Max(Periods[0], System.Linq.Enumerable.Max(Periods)) : 0;
    }

    public sealed class AtrSetting
    {
        public bool Enabled { get; init; } = true;
        public int Period { get; init; } = 14;   // Wilder ATR 常用 14
        public int Warmup => Period;
    }

    public class OrbSettings
    {
        public string Start { get; set; } = "09:00:00";
        public string End { get; set; } = "09:15:00";

        // ★ 請確保這兩個屬性存在，且名稱對應 appsettings.json
        public string NightStart { get; set; } = "15:00:00";
        public string NightEnd { get; set; } = "15:30:00";
    }
}

