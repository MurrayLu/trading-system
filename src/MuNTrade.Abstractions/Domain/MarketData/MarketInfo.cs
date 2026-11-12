using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.MarketData
{
    /// <summary>
    /// 彙整後的商品市場資訊單一真相。
    /// </summary>
    public record MarketInfo(
        string Symbol,
        MarketNo MarketNo,
        MarketKind Kind,
        MarketSource Source
    )
    {
        public int DecimalPlaces { get; init; } = 0;

        public bool IsSecurities => Kind == MarketKind.Securities;
        public bool IsDerivatives => Kind == MarketKind.Derivatives;
        public bool IsOverseas => Source == MarketSource.Overseas;

        public static MarketInfo Unknown(string symbol) => new(symbol, MarketNo.Unknown, MarketKind.Unknown, MarketSource.Domestic);
    }
}

