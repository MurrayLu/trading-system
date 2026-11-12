// MuNTrade.Abstractions/Settings/ApplicationSettings.cs
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;

namespace MuNTrade.Abstractions.Settings.Common
{
    public class ApplicationSettings
    {
        public string TradingGateway { get; set; } = "PAPER";
        public AppMode AppMode { get; set; } = AppMode.Execution; // Default
        public bool EnableHeavyUiConsumers { get; set; } = true;
    }
}

