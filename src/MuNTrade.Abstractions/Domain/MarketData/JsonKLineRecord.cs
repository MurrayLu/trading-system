using System;
using System.Text.Json.Serialization;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Domain.MarketData
{

    public class JsonKLineRecord
    {
        // 明確對外輸出欄位名稱為 "KlineTime"
        [JsonPropertyName("KlineTime")]
        public DateTime KlineTime { get; set; }

        [JsonPropertyName("Open")]
        public double Open { get; set; }

        [JsonPropertyName("High")]
        public double High { get; set; }

        [JsonPropertyName("Low")]
        public double Low { get; set; }

        [JsonPropertyName("Close")]
        public double Close { get; set; }

        // 明確對外輸出欄位名稱為 "Qty"
        [JsonPropertyName("Qty")]
        public long Qty { get; set; }

        // 如果你之前用 Volume/From/To，建議保留對應的舊屬性（非序列化）以便程式內部相容
        [JsonIgnore]
        public DateTime From => KlineTime;

        [JsonIgnore]
        public DateTime To => KlineTime;

        [JsonIgnore]
        public long Volume => Qty;

        [JsonIgnore]
        public ExchangeTs ExchangeTs => new ExchangeTs(KlineTime);
    }
}

