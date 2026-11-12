namespace MuNTrade.Core.Settings
{
    public class CapitalApiSettings
    {
        public string UserId { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Mode { get; set; } = "Backtest"; // 將 Mode 屬性加回來
    }
}
