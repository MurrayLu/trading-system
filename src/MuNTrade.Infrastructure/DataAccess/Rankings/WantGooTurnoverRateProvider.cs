using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Dtos.Rank;

namespace MuNTrade.Infrastructure.DataAccess.Rankings
{
    public sealed class WantGooTurnoverRateProvider : IWantGooTurnoverRateProvider
    {
        private static readonly Uri TargetUrl = new("https://www.wantgoo.com/stock/all-turnover-rates");
        private static readonly HttpClient Http = CreateHttpClient();

        private readonly ILogger<WantGooTurnoverRateProvider> _logger;

        public WantGooTurnoverRateProvider(ILogger<WantGooTurnoverRateProvider> logger)
        {
            _logger = logger;
        }

        public async Task<IReadOnlyList<WantGooTurnoverRateRawItem>> FetchAllAsync(CancellationToken ct)
        {
            var json = await FetchJsonAsync(TargetUrl, ct);
            var items = ParseItems(json);

            if (items.Count == 0)
                _logger.LogWarning("[WantGooTurnoverRateProvider] Parsed 0 items.");

            return items;
        }

        private static List<WantGooTurnoverRateRawItem> ParseItems(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return ParseArray(root);

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return ParseArray(data);

            return new List<WantGooTurnoverRateRawItem>();
        }

        private static List<WantGooTurnoverRateRawItem> ParseArray(JsonElement array)
        {
            var list = new List<WantGooTurnoverRateRawItem>(array.GetArrayLength());
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string? id = null;
                decimal value = 0m;

                if (item.TryGetProperty("investrueId", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.String)
                        id = idProp.GetString();
                    else if (idProp.ValueKind == JsonValueKind.Number)
                        id = idProp.GetRawText();
                }

                if (item.TryGetProperty("value", out var valueProp))
                {
                    if (valueProp.ValueKind == JsonValueKind.Number)
                        value = valueProp.GetDecimal();
                    else if (valueProp.ValueKind == JsonValueKind.String)
                        decimal.TryParse(valueProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
                }

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                list.Add(new WantGooTurnoverRateRawItem
                {
                    InvestrueId = id,
                    Value = value
                });
            }

            return list;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/121.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en-US;q=0.8,en;q=0.7");

            return client;
        }

        private static async Task<string> FetchJsonAsync(Uri url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}

