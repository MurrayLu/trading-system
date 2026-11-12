// 檔案路徑: MuNTrade.Abstractions/Domain/Models/DataSegment.cs (v1.2 - JSON 同步版)

using System;
using System.Text.Json.Serialization;

namespace MuNTrade.Abstractions.Domain.Optimization
{
    /// <summary>
    /// 代表一段用於回測或分析的資料區間。
    /// 【v1.2】修改結構以匹配 appsettings.json 的格式。
    /// </summary>
    public class DataSegment
    {
        public string Symbol { get; set; } = string.Empty;

        // 這些屬性會直接對應 JSON 中的 "Date", "From", "To"
        public DateOnly Date { get; set; }
        public TimeOnly From { get; set; }
        public TimeOnly To { get; set; }

        /// <summary>
        /// (唯讀) 將 Date 和 From 組合為完整的開始時間 DateTime。
        /// JsonIgnore 屬性會告訴序列化器在寫入 JSON 時忽略此屬性。
        /// </summary>
        [JsonIgnore]
        public DateTime Start => Date.ToDateTime(From);

        /// <summary>
        /// (唯讀) 將 Date 和 To 組合為完整的結束時間 DateTime。
        /// </summary>
        [JsonIgnore]
        public DateTime End => Date.ToDateTime(To);
    }
}
