namespace MuNTrade.Abstractions.Dtos.Trading
{
    /// <summary>
    /// 價位等級：包含價格與對應的數量
    /// </summary>
    /// <param name=""Price""></param>
    /// <param name=""Volume""></param>
    public sealed record PriceLevel(decimal Price, int Volume);
}
