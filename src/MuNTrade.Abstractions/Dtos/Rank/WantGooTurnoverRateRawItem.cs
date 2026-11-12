using System.Text.Json.Serialization;

namespace MuNTrade.Abstractions.Dtos.Rank
{
    public sealed class WantGooTurnoverRateRawItem
    {
        [JsonPropertyName("investrueId")]
        public string InvestrueId { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public decimal Value { get; set; }
    }
}

