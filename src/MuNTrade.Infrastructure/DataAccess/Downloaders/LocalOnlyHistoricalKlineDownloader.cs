using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Warmup;
using MuNTrade.Abstractions.Domain.MarketData;

namespace MuNTrade.Infrastructure.DataAccess.Downloaders
{
    /// <summary>
    /// Backtest / governed batch 專用的離線 downloader。
    /// 不嘗試連線 broker，也不補資料；僅回傳空結果並留下簡短紀錄。
    /// </summary>
    public sealed class LocalOnlyHistoricalKlineDownloader : IHistoricalKlineDownloader
    {
        private readonly ILogger<LocalOnlyHistoricalKlineDownloader> _log;

        public LocalOnlyHistoricalKlineDownloader(ILogger<LocalOnlyHistoricalKlineDownloader> log)
        {
            _log = log;
        }

        public Task<IReadOnlyList<JsonKLineRecord>> DownloadAsync(
            string symbol,
            string timeframeKey,
            DateOnly start,
            DateOnly lcd,
            CancellationToken ct = default)
        {
            _log.LogWarning(
                "[WarmupMissingData] Local-only mode cannot download missing KLine data. symbol={Symbol} timeframe={Timeframe} range={Start}~{End}",
                symbol,
                timeframeKey,
                start,
                lcd);

            return Task.FromResult<IReadOnlyList<JsonKLineRecord>>(Array.Empty<JsonKLineRecord>());
        }
    }
}
