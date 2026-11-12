namespace MuNTrade.Abstractions.Events.Strategy
{
    public enum StrategyCategory
    {
        Observation, // Market data or indicator analysis
        Decision,    // Logic or signal generation
        Intent,      // Trading action plan
        State,       // State machine transitions
        Risk,        // Risk management checks
        Debug,       // Internal diagnostics
        System       // System level events (e.g. Probe)
    }
}
