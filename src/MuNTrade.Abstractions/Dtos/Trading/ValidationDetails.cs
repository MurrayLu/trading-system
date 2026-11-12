using MuNTrade.Abstractions.Domain.Trading.Enums;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record ValidationDetails(
        int TrendScore,                 // 0-100
        PullbackQuality PullbackQuality,
        decimal ContinuationVolumeRatio // 突破棒量 / 回檔均量
    );
}
