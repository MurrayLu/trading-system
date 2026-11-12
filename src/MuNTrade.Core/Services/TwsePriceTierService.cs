using MuNTrade.Abstractions.Contracts.Services;
using Microsoft.Extensions.Options;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 台灣證券交易所 (TWSE) 的股價跳動單位/級距服務。
    ///
    /// 注意：
    ///TickSizeAtPrice(price) = 「此價位所屬級距的 tick」
    ///NextUp/NextDown = 「從此價位往上/往下一檔的合法價位」
    ///
    /// 在級距邊界 (例如 1000)：
    ///NextUp(1000) = 1005
    ///NextDown(1000) = 999
    /// </summary>
    public class TwsePriceTierService : IPriceTierService
    {
        private readonly IOptionsMonitor<CapitalControlOptions> _options;

        public TwsePriceTierService(IOptionsMonitor<CapitalControlOptions> options)
        {
            _options = options;
        }

        /// <summary>
        /// 只回傳「此價位所屬級距」的 tick size。
        /// ⚠️不要用它直接做 price ± tick 來算下一檔/上一檔，邊界會出錯。
        /// </summary>
        public decimal GetTickPrice(decimal price)
            => GetTickSizeAtPrice(price);

        /// <summary>
        /// 回傳此價位所在級距的 tick size。
        /// </summary>
        public decimal GetTickSizeAtPrice(decimal price)
        {
            var rules = GetNormalizedRulesOrNull();
            if (rules != null)
            {
                var idx = FindRuleIndex(rules, price);
                if (idx >= 0) return rules[idx].TickSize;
            }

            // Fallback: TWSE 標準 (硬編碼)
            return GetFallbackTickSize(price);
        }

        /// <summary>
        /// 往上跳一檔（下一個合法價位）。
        /// </summary>
        public decimal GetNextUpPrice(decimal price)
        {
            var tick = GetTickSizeForMove(price, MoveDirection.Up);
            return price + tick;
        }

        /// <summary>
        /// 往下跳一檔（下一個合法價位）。
        /// </summary>
        public decimal GetNextDownPrice(decimal price)
        {
            var tick = GetTickSizeForMove(price, MoveDirection.Down);
            return price - tick;
        }

        /// <summary>
        /// 往上/往下移動 N 檔（N 可為負數）。
        /// 這會在每一步重新評估 tick（避免跨級距時跳錯）。
        /// </summary>
        public decimal MoveByTicks(decimal price, int ticks)
        {
            if (ticks == 0) return price;

            var direction = ticks > 0 ? MoveDirection.Up : MoveDirection.Down;
            var steps = Math.Abs(ticks);

            var p = price;
            for (int i = 0; i < steps; i++)
            {
                p = direction == MoveDirection.Up ? GetNextUpPrice(p) : GetNextDownPrice(p);
            }
            return p;
        }


        // Internal helpers


        private enum MoveDirection { Up, Down }

        private decimal GetTickSizeForMove(decimal price, MoveDirection direction)
        {
            // ✅關鍵：下跳在「級距下界」要用上一個級距的 tick
            // 例：price=1000，所在級距 tick=5，但往下應該用 1（因為 999 在 <1000 的級距）
            var rules = GetNormalizedRulesOrNull();
            if (rules != null)
            {
                var idx = FindRuleIndex(rules, price);
                if (idx >= 0)
                {
                    var rule = rules[idx];

                    if (direction == MoveDirection.Down && price == rule.MinPrice)
                    {
                        // 若剛好在此級距的 MinPrice，下跳應使用上一級距 tick
                        if (idx > 0)
                            return rules[idx - 1].TickSize;

                        // 如果 idx==0 表示已經在最底層級距下界，再往下就還是用本級距 tick
                        // （也可以改成 throw，視既有 系統規則）
                        return rule.TickSize;
                    }

                    // 其它情況：上跳用當前級距 tick；下跳非下界也用當前級距 tick
                    return rule.TickSize;
                }
            }

            // Fallback：沒有規則表或找不到命中區間，就用硬編碼級距
            // 同樣要處理「下跳在邊界」：用一個純函式判斷
            if (direction == MoveDirection.Down)
                return GetFallbackTickSizeForDownMove(price);

            return GetFallbackTickSize(price);
        }

        private List<TickRule>? GetNormalizedRulesOrNull()
        {
            var raw = _options.CurrentValue?.StockRules?.TickRules;
            if (raw == null || raw.Count == 0) return null;

            // Sort rules so the matching price band is selected deterministically.
            var rules = raw
                .Select(r => new TickRule(r.MinPrice, r.MaxPrice, r.TickSize))
                .OrderBy(r => r.MinPrice)
                .ToList();

            // ✅（可選但我強烈建議）做基本防呆：避免重疊/反向/空 tick

            return rules;
        }

        private int FindRuleIndex(List<TickRule> rules, decimal price)
        {
            // 線性找即可（區間數通常很少）；要最佳化可改 binary search
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (price >= r.MinPrice && price < r.MaxPrice) return i;
            }
            return -1;
        }

        private static void ValidateRules(List<TickRule> rules)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];

                if (r.MinPrice >= r.MaxPrice)
                    throw new InvalidOperationException($"TickRules invalid: MinPrice >= MaxPrice at index {i}.");

                if (r.TickSize <= 0)
                    throw new InvalidOperationException($"TickRules invalid: TickSize <= 0 at index {i}.");

                if (i > 0)
                {
                    var prev = rules[i - 1];

                    // 允許「貼齊」：prev.MaxPrice == r.MinPrice 是正常分段
                    if (prev.MaxPrice > r.MinPrice)
                        throw new InvalidOperationException($"TickRules overlap: index {i - 1} and {i}.");
                }
            }
        }


        // Fallback (hard-coded TWSE)


        private static decimal GetFallbackTickSize(decimal price)
        {
            if (price < 10m) return 0.01m;
            if (price < 50m) return 0.05m;
            if (price < 100m) return 0.1m;
            if (price < 500m) return 0.5m;
            if (price < 1000m) return 1m;
            return 5m;
        }

        /// <summary>
        /// Fallback 下跳用：在邊界要用下一個更低級距的 tick。
        /// 例如 price=1000，下跳應用 1（因為下一個價位是 999）。
        /// </summary>
        private static decimal GetFallbackTickSizeForDownMove(decimal price)
        {
            // 邊界集合：10, 50, 100, 500, 1000 ...
            // 下跳在邊界時，tick 應該取「邊界以下」那段。
            // 例如 50 的下一檔應該是 49.95（0.05）。
            if (price == 10m) return 0.01m;
            if (price == 50m) return 0.05m;
            if (price == 100m) return 0.1m;
            if (price == 500m) return 0.5m;
            if (price == 1000m) return 1m;

            // 非邊界就用目前所在級距 tick
            return GetFallbackTickSize(price);
        }

        // 本地規則 DTO（避免依賴外部設定類型的具體型別）
        private sealed record TickRule(decimal MinPrice, decimal MaxPrice, decimal TickSize);
    }
}
