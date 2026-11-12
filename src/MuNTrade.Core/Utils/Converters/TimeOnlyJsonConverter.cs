using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuNTrade.Core.Utils.Converters
{
    /// <summary>
    // 提供 System.Text.Json 對 .NET 6+ 的 TimeOnly 型別的序列化與反序列化支援。
    /// 使用 "O" (round-trip) 格式以包含毫秒精度。
    /// </summary>
    public class TimeOnlyJsonConverter : JsonConverter<TimeOnly>
    {
        private const string Format = "O"; // "O" 是標準的往返格式，例如 HH:mm:ss.fffffff

        /// <summary>
        /// 從 JSON 字串讀取並轉換為 TimeOnly。
        /// </summary>
        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var timeString = reader.GetString();
            if (string.IsNullOrWhiteSpace(timeString))
            {
                throw new JsonException("TimeOnly string cannot be null or empty.");
            }
            return TimeOnly.Parse(timeString, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 將 TimeOnly 物件寫入為 JSON 字串。
        /// </summary>
        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}
