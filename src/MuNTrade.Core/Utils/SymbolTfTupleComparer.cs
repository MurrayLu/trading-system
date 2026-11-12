using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Utils
{
    public sealed class SymbolTfTupleComparer : IEqualityComparer<(string sym, string tf)>
    {
        public static readonly SymbolTfTupleComparer Instance = new SymbolTfTupleComparer();

        public bool Equals((string sym, string tf) x, (string sym, string tf) y)
        {
            return string.Equals(x.sym, y.sym, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.tf, y.tf, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string sym, string tf) obj)
        {
            // 合理地合成兩個 string 的 hash (忽略大小寫)
            unchecked
            {
                int h1 = obj.sym != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj.sym) : 0;
                int h2 = obj.tf != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj.tf) : 0;
                return (h1 * 397) ^ h2;
            }
        }
    }
}

