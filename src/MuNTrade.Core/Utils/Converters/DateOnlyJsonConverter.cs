using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuNTrade.Core.Utils.Converters
{
    /// <summary>
    /// 提供 System.Text.Json 對 .NET 6+ 的 DateOnly 型別的序列化與反序列化支援。
    /// </summary>
    public class DateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private const string Format = "yyyy-MM-dd";

        /// <summary>
        /// 從 JSON 字串讀取並轉換為 DateOnly。
        /// </summary>
        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var dateString = reader.GetString();
                if (string.IsNullOrWhiteSpace(dateString))
                {
                    throw new JsonException("DateOnly string cannot be null or empty.");
                }

                return DateOnly.ParseExact(dateString, Format, CultureInfo.InvariantCulture);
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;

                if (root.TryGetProperty("year", out var yearProp) &&
                    root.TryGetProperty("month", out var monthProp) &&
                    root.TryGetProperty("day", out var dayProp))
                {
                    return new DateOnly(yearProp.GetInt32(), monthProp.GetInt32(), dayProp.GetInt32());
                }

                if (root.TryGetProperty("dayNumber", out var dayNumberProp))
                {
                    return DateOnly.FromDayNumber(dayNumberProp.GetInt32());
                }

                throw new JsonException("DateOnly object must contain year/month/day or dayNumber.");
            }

            throw new JsonException($"Unsupported token for DateOnly: {reader.TokenType}.");
        }

        /// <summary>
        /// 將 DateOnly 物件寫入為 JSON 字串。
        /// </summary>
        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}

