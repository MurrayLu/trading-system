using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Domain.Rank;
using MuNTrade.Abstractions.Dtos.Rank;
using MuNTrade.Abstractions.Settings.Rank;

namespace MuNTrade.Core.Services.Rank
{
    public sealed class WantGooTurnoverRankService
    {
        private readonly IWantGooTurnoverRateProvider _provider;
        private readonly IStockMasterIndexProvider _stockIndex;
        private readonly IOptions<RankBoardSettings> _settings;
        private readonly ILogger<WantGooTurnoverRankService> _logger;

        public WantGooTurnoverRankService(
            IWantGooTurnoverRateProvider provider,
            IStockMasterIndexProvider stockIndex,
            IOptions<RankBoardSettings> settings,
            ILogger<WantGooTurnoverRankService> logger)
        {
            _provider = provider;
            _stockIndex = stockIndex;
            _settings = settings;
            _logger = logger;
        }

        public async Task<TurnoverRankResult> FetchAsync(CancellationToken ct = default)
        {
            var cfg = _settings.Value.WantGoo;
            if (!cfg.Enabled)
            {
                return new TurnoverRankResult(
                    DateTimeOffset.Now,
                    0,
                    0,
                    0,
                    0,
                    Array.Empty<TurnoverRankItem>());
            }

            var raw = await _provider.FetchAllAsync(ct);

            int totalCount = raw.Count;
            int stockCount = 0;
            int nonStockCount = 0;
            int unknownCount = 0;

            var list = new List<TurnoverRankItem>(raw.Count);
            bool stockOnly = cfg.StockOnly;

            foreach (var item in raw)
            {
                var id = item.InvestrueId?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    unknownCount++;
                    if (!stockOnly)
                    {
                        list.Add(new TurnoverRankItem
                        {
                            Id = string.Empty,
                            Name = "-",
                            Market = "-",
                            TurnoverRatePercent = item.Value,
                            Kind = InstrumentKind.Unknown
                        });
                    }
                    continue;
                }

                bool existsInIndex = _stockIndex.TryGet(id, out var entry);
                var kind = InstrumentClassifier.Classify(id, existsInIndex);
                switch (kind)
                {
                    case InstrumentKind.Stock:
                        stockCount++;
                        break;
                    case InstrumentKind.NonStock:
                        nonStockCount++;
                        break;
                    default:
                        unknownCount++;
                        break;
                }

                if (stockOnly && kind != InstrumentKind.Stock)
                    continue;

                list.Add(new TurnoverRankItem
                {
                    Id = id,
                    Name = kind == InstrumentKind.Stock ? entry.StockName : "-",
                    Market = kind == InstrumentKind.Stock ? entry.Market : "-",
                    TurnoverRatePercent = item.Value,
                    Kind = kind
                });
            }

            list.Sort((a, b) =>
            {
                int cmp = b.TurnoverRatePercent.CompareTo(a.TurnoverRatePercent);
                if (cmp != 0) return cmp;
                return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });

            int topN = cfg.TopN;
            if (topN < 0) topN = 0;
            if (topN > 0 && list.Count > topN)
                list = list.Take(topN).ToList();

            for (int i = 0; i < list.Count; i++)
            {
                list[i] = new TurnoverRankItem
                {
                    Rank = i + 1,
                    Id = list[i].Id,
                    Name = list[i].Name,
                    Market = list[i].Market,
                    TurnoverRatePercent = list[i].TurnoverRatePercent,
                    Kind = list[i].Kind
                };
            }

            var result = new TurnoverRankResult(
                DateTimeOffset.Now,
                totalCount,
                stockCount,
                nonStockCount,
                unknownCount,
                list);

            _logger.LogInformation(
                "[WantGooTurnoverRank] Total={Total} Stock={Stock} NonStock={NonStock} Unknown={Unknown} Items={Items}",
                totalCount, stockCount, nonStockCount, unknownCount, list.Count);

            return result;
        }
    }
}

