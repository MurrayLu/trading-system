namespace MuNTrade.Abstractions.Domain.OrderBook;

public sealed record LiquidityTimeGuards(
    int ExposeMs,
    int ShiftMs,
    int RestMs,
    int BacktrackMs,
    int RemovalDelayMs);

public enum LiquidityGuardReason
{
    None,
    Exposed,        // Block Add (Rule 1)
    Shifted,        // Block Add (Rule 2)
    Young,          // Block Pull (Rule 3)
    Backtrack,      // Block Pull (Rule 5)
    Absorbed,       // Synonym for Backtrack
    Quality,        // Blocked by Bad Quality
    NewLevel        // Blocked by Rule 1 (Creation tick)
}
