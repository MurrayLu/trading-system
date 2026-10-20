using System.Collections.Generic;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    // --- L2 簡化快照（足夠支援 ExecutionQualityGuard） ---
    public sealed record BestLevel(decimal Price, int Quantity);
}
