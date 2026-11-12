using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record MissedOpportunity(
        ValidatedOpportunity Opportunity,
        MissedReason Reason,
        ArrivalTs LoggedAt
    );
}
