namespace MuNTrade.Abstractions.Helpers
{
    public static class StrategyIdBuilder
    {
        public static string BuildEngineInstanceId(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return "Engine#Unknown#Default";
            return $"Engine#{symbol}#Default";
        }
    }
}

