using MuNTrade.Abstractions.Domain.Trading.Enums;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    //下單意圖/結果（若已有 OrderRequest / OrderFillEvent，可做 adapter） ---
    public sealed record ExecutionPlan(
        int Quantity,                   // 張數或股數（配合 LotSize）
        OrderType OrderType,
        TimeInForce Tif,
        decimal? LimitPrice,
        bool AllowSlipUpOneTick,
        int MaxRetries,
        decimal? StopLossPrice,
        decimal? TakeProfitPrice
    );
}
