using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Dtos.Rank;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Services.Rank
{
    public sealed class DailyTurnoverRankService
    {
        private readonly WantGooTurnoverRankService _rankService;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IClock _clock;
        private readonly DataSourceSettings _dataSettings;
        private readonly ILogger<DailyTurnoverRankService> _logger;

        public DailyTurnoverRankService(
            WantGooTurnoverRankService rankService,
            ITradingSessionProvider sessionProvider,
            IClock clock,
            IOptions<DataSourceSettings> dataSettings,
            ILogger<DailyTurnoverRankService> logger)
        {
            _rankService = rankService;
            _sessionProvider = sessionProvider;
            _clock = clock;
            _dataSettings = dataSettings.Value;
            _logger = logger;
        }

        public async Task<TurnoverRankUpdateResult> UpdateAsync(CancellationToken ct = default)
        {
            var tradeDate = ResolveTradeDate(_clock.Now);
            var baseDir = _dataSettings.TurnoverCsvBasePath;
            if (string.IsNullOrWhiteSpace(baseDir))
                throw new InvalidOperationException("TurnoverCsvBasePath is not configured.");

            Directory.CreateDirectory(baseDir);

            var result = await _rankService.FetchAsync(ct).ConfigureAwait(false);
            var filePath = Path.Combine(baseDir, $"turnover_{tradeDate:yyyyMMdd}.csv");

            await WriteCsvAsync(filePath, result, ct).ConfigureAwait(false);

            _logger.LogInformation("[DailyTurnoverRank] Wrote {Count} items to {Path}", result.Items.Count, filePath);

            return new TurnoverRankUpdateResult(tradeDate, result.Items.Count, filePath);
        }

        private DateOnly ResolveTradeDate(DateTime now)
        {
            var session = _sessionProvider.GetSession("TX00", now);
            if (session != null)
                return session.TradeDate;

            var td = now.TimeOfDay >= new TimeSpan(15, 0, 0) ? now.Date.AddDays(1) : now.Date;
            return DateOnly.FromDateTime(td);
        }

        private static Task WriteCsvAsync(string path, TurnoverRankResult result, CancellationToken ct)
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("Symbol,Turnover");
            foreach (var item in result.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                sb.Append(item.Id.Trim());
                sb.Append(',');
                sb.Append(item.TurnoverRatePercent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            return File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
        }
    }
}

