namespace MuNTrade.Abstractions.Settings.Rank
{
    public sealed class RankBoardSettings
    {
        public WantGooRankSettings WantGoo { get; set; } = new();
    }

    public sealed class WantGooRankSettings
    {
        public bool Enabled { get; set; } = true;
        public int TopN { get; set; } = 50;
        public bool StockOnly { get; set; } = true;
    }
}

