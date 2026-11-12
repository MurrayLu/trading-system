using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Domain.Session
{
    // =========================
    // Small DTOs used by provider
    // =========================

    public record PeriodWindow(
        DateTime WindowStart,
        DateTime WindowEnd,
        DateOnly FirstTradingDate,
        DateOnly LastTradingDate,
        string Note = "");
}

