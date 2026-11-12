using System;

namespace MuNTrade.Abstractions.Domain.Trading
{
    public static class OrderKeyHelpers
    {
        public static bool IsValidBookNo(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            if (trimmed.Length >= 5)
            {
                bool allZero = true;
                for (int i = 0; i < trimmed.Length; i++)
                {
                    if (trimmed[i] != '0')
                    {
                        allZero = false;
                        break;
                    }
                }
                if (allZero) return false;
            }

            return true;
        }
    }
}

