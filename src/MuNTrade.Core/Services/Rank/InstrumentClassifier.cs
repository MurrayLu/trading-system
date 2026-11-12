using MuNTrade.Abstractions.Domain.Rank;

namespace MuNTrade.Core.Services.Rank
{
    public static class InstrumentClassifier
    {
        public static InstrumentKind Classify(string? id, bool existsInIndex)
        {
            if (string.IsNullOrWhiteSpace(id))
                return InstrumentKind.Unknown;

            bool allDigits = true;
            for (int i = 0; i < id.Length; i++)
            {
                if (!char.IsDigit(id[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (!allDigits)
                return InstrumentKind.NonStock;

            if (id.Length >= 2 && id[0] == '0' && id[1] == '0')
                return InstrumentKind.NonStock;

            return existsInIndex ? InstrumentKind.Stock : InstrumentKind.NonStock;
        }
    }
}

