namespace MuNTrade.Abstractions.Dtos.OrderBook
{
    public record FlowWindowStats(
        int WindowMs,
        double P90Add,
        double P90Pull,
        double LastAdd,
        double LastPull,
        int Samples
    )
    {
        public static readonly FlowWindowStats Zero = new(1000, 0, 0, 0, 0, 0);
    }
}
