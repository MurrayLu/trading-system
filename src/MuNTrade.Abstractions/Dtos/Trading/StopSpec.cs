namespace MuNTrade.Abstractions.Dtos.Trading
{
    //風控/出場 規格 ---
    public sealed record StopSpec(decimal Price, string Reason);
}
