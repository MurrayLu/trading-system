using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Infrastructure.DataAccess.Time;
using Serilog;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.DataAccess.FileSystem
{
    public partial class HistoricalDataReader
    {
        /// <summary>
        /// 以「格式 B」儲存月度檔案（每筆為 KlineTime/Open/High/Low/Close/Qty）
        /// 這會把內部的 JsonKLineRecord 轉換成指定的 canonical B schema，並落地為月度檔案。
        /// </summary>
        public async Task SaveMonthlyRollingAsync(
            string stockId,
            string prefix,
            DateOnly lcd,
            IEnumerable<JsonKLineRecord> rows,
            CancellationToken ct = default)
        {
            var dir = Path.Combine(_basePath, stockId);
            Directory.CreateDirectory(dir);

            // 按 record 所屬月份分組（以 To 的年/月）
            var groups = rows
                .GroupBy(r => new { Year = r.To.Year, Month = r.To.Month })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

            foreach (var g in groups)
            {
                var rowsInGroup = g.ToList();

                // 月曆上的起訖（1日與月末）
                var monthStart = new DateOnly(g.Key.Year, g.Key.Month, 1);
                var monthEndCalendar = DateOnly.FromDateTime(new DateTime(g.Key.Year, g.Key.Month, DateTime.DaysInMonth(g.Key.Year, g.Key.Month)));

                // 實際 rows 的 min/max（以 r.To 為依據）。若沒有 rows，回退為 monthStart/monthEnd
                DateOnly actualMin = rowsInGroup.Count > 0
                    ? DateOnly.FromDateTime(rowsInGroup.Min(r => r.To))
                    : monthStart;
                DateOnly actualMax = rowsInGroup.Count > 0
                    ? DateOnly.FromDateTime(rowsInGroup.Max(r => r.To))
                    : monthEndCalendar;

                // 判斷是否為目前月份（以 lcd 判斷）
                bool isCurrentMonth = (lcd.Year == g.Key.Year && lcd.Month == g.Key.Month);

                // 決定檔名的 from/to（保險規則）
                DateOnly fileFrom;
                DateOnly fileTo;

                if (!isCurrentMonth)
                {
                    // 過去已封閉月份：強制使用整個月（1日 ~ 月末）作為檔名區間（既有 保險）
                    fileFrom = monthStart;
                    fileTo = monthEndCalendar;
                }
                else
                {
                    // 目前月份：用 monthStart ~ lcd（但若 rows 的實際範圍超出 lcd，則擴展到實際範圍；不會縮短）
                    fileFrom = monthStart;
                    fileTo = lcd;

                    if (actualMin < fileFrom) fileFrom = actualMin;
                    if (actualMax > fileTo) fileTo = actualMax;
                }

                // 防護：避免 fileTo < fileFrom
                if (fileTo < fileFrom) fileTo = fileFrom;

                // 刪除同月舊檔（以 yyyyMM token 為 key 確保一個月只留一份檔案）
                var yearMonthToken = monthStart.ToString("yyyyMM");
                var existingSameMonth = Directory.GetFiles(dir, $"{prefix}_*.json")
                                                 .Where(f => Path.GetFileNameWithoutExtension(f).Contains(yearMonthToken))
                                                 .ToArray();

                foreach (var old in existingSameMonth)
                {
                    try
                    {
                        // 顯示會刪掉哪個檔案（debug）
                        _log.LogDebug("[K線儲存] Delete old monthly file for same month: {Old}", old);
                        File.Delete(old);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[K線儲存] 刪除舊檔失敗：{Old}", old);
                    }
                }

                var path = Path.Combine(dir, $"{prefix}_{fileFrom:yyyyMMdd}_{fileTo:yyyyMMdd}.json");

                // 寫檔（序列化 rowsInGroup）
                try
                {
                    await using var fs = File.Create(path);
                    await JsonSerializer.SerializeAsync(fs, rowsInGroup, _jsonOptions, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[K線儲存] 寫入 Monthly Rolling JSON 失敗：{Path}", path);
                    throw;
                }

                // 記錄 debug 資訊：檔名、rows 數、rows 的實際 min/max
                _log.LogDebug("[K線儲存] Saved Monthly Rolling JSON : {File} (rows={Rows})", path, rowsInGroup.Count);
                _log.LogDebug("[K線儲存] File range for {File}: fileFrom={FileFrom}, fileTo={FileTo}, actualMin={ActualMin}, actualMax={ActualMax}, lcd={LCD}",
                    path, fileFrom, fileTo, actualMin, actualMax, lcd);
            }
        }
    }
}

