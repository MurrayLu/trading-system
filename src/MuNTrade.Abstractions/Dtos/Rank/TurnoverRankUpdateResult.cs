using System;

namespace MuNTrade.Abstractions.Dtos.Rank
{
    public sealed record TurnoverRankUpdateResult(
        DateOnly TradeDate,
        int ItemCount,
        string CsvPath);
}

