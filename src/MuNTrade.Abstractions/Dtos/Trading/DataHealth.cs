namespace MuNTrade.Abstractions.Dtos.Trading
{
    // --- 健康度 / 機會成本 ---
    public sealed record DataHealth(HealthLevel Level, string? Message = null);
}
