using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Dtos.Rank;

namespace MuNTrade.Infrastructure.DataAccess.Rankings
{
    [Obsolete("Yahoo turnover provider is deprecated. WantGoo-only rank is used.")]
    public sealed class YahooTurnoverRankProvider : IYahooTurnoverRankProvider
    {
        private static readonly Uri TargetUrl = new("https://tw.stock.yahoo.com/rank/turnover");
        private static readonly Regex SymbolRegex = new(@"\b\d{4}\.(TW|TWO)\b", RegexOptions.Compiled);
        private static readonly HttpClient Http = CreateHttpClient();

        private readonly ILogger<YahooTurnoverRankProvider> _logger;

        public YahooTurnoverRankProvider(ILogger<YahooTurnoverRankProvider> logger)
        {
            _logger = logger;
        }

        public async Task<YahooTurnoverRankSnapshot> FetchTopAsync(int topN, CancellationToken ct)
        {
            if (topN <= 0)
                return new YahooTurnoverRankSnapshot(DateTimeOffset.Now, Array.Empty<YahooTurnoverRankEntry>());

            var html = await FetchHtmlAsync(TargetUrl, ct);
            var items = new List<YahooTurnoverRankEntry>(Math.Min(topN, 200));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rank = 0;

            foreach (Match match in SymbolRegex.Matches(html))
            {
                var raw = match.Value;
                if (raw.Length < 4)
                    continue;

                var stockNo = raw.Substring(0, 4);
                if (!seen.Add(stockNo))
                    continue;

                rank++;
                items.Add(new YahooTurnoverRankEntry(raw, stockNo, rank));

                if (items.Count >= topN)
                    break;
            }

            if (items.Count < topN)
            {
                _logger.LogWarning("[YahooTurnoverRankProvider] Parsed {Count} items (target {Target}).", items.Count, topN);
            }

            return new YahooTurnoverRankSnapshot(DateTimeOffset.Now, items);
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
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en-US;q=0.8,en;q=0.7");

            return client;
        }

        private static async Task<string> FetchHtmlAsync(Uri url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}

