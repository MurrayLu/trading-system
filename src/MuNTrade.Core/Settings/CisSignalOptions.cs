namespace MuNTrade.Core.Settings
{
    public sealed class CisSignalOptions
    {
        /// <summary>
        /// SignalOnly | Trade
        /// </summary>
        public string Mode { get; set; } = "SignalOnly";

        public int DefaultQuantity { get; set; } = 1;
    }
}

