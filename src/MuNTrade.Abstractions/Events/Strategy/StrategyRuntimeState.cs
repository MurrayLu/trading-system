namespace MuNTrade.Abstractions.Events.Strategy
{
    /// <summary>
    /// Strict lifecycle state for the Strategy Runtime.
    /// Acts as the source of truth for "Hard Stop" gating.
    /// </summary>
    public enum StrategyRuntimeState
    {
        Initializing,
        Running,
        Stopping,
        Stopped,
        Faulted
    }
}

