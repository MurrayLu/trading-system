namespace MuNTrade.Abstractions.Settings.Data
{
    /// <summary>
    /// 定義一根 Bar 的規格。
    /// </summary>
    public class BarConfig
    {
        public string Type { get; set; } = "Time"; // Time, Day, Week, Month, Tick
        public int Period { get; set; }
        public string? Key { get; set; }
        public bool EmitPartial { get; set; } = true; // default true for backwards compatibility
    }
}
