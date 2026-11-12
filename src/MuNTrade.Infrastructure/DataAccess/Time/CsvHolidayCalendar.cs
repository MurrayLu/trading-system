using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MuNTrade.Infrastructure.DataAccess.Time
{
    /// <summary>
    /// 從 CSV 檔案讀取假日設定的日曆實作。
    /// </summary>
    public class CsvHolidayCalendar : IHolidayCalendar
    {
        private readonly ILogger<CsvHolidayCalendar> _logger;
        private readonly HashSet<DateOnly> _holidays;

        public CsvHolidayCalendar(ILogger<CsvHolidayCalendar> logger, IOptions<DataSourceSettings> dataSourceSettings)
        {
            _logger = logger;
            // 使用 HashSet 提供 O(1) 的高效查詢性能
            _holidays = new HashSet<DateOnly>();

            // 從設定檔中讀取假日檔案的路徑
            var filePath = ResolvePath(dataSourceSettings.Value.HolidayCalendarPath);

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("假日日曆檔案路徑未設定或檔案不存在: {FilePath}。日曆將只考慮週末。", filePath);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    // 忽略註解和空行
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    {
                        continue;
                    }

                    if (DateOnly.TryParse(line.Trim(), out var holiday))
                    {
                        _holidays.Add(holiday);
                    }
                }
                _logger.LogInformation("成功從 {FilePath} 載入 {Count} 個國定假日。", filePath, _holidays.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "讀取假日日曆檔案 {FilePath} 時發生錯誤。", filePath);
            }
        }

        public bool IsHoliday(DateOnly date)
        {
            // 優先檢查是否為國定假日，然後再檢查是否為週末
            return _holidays.Contains(date) || date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
        }

        public DateOnly GetNextTradingDay(DateOnly date)
        {
            var nextDay = date.AddDays(1);
            // 持續往後找，直到找到一個不是假日的日期
            while (IsHoliday(nextDay))
            {
                nextDay = nextDay.AddDays(1);
            }
            return nextDay;
        }

        public DateOnly GetPreviousTradingDay(DateOnly date)
        {
            if (date <= DateOnly.MinValue.AddDays(1))
            {
                _logger.LogWarning(
                    "GetPreviousTradingDay 收到過小日期 {Date}，已回退為 DateOnly.MinValue 以避免溢位。",
                    date);
                return DateOnly.MinValue;
            }

            var previousDay = date.AddDays(-1);

            // 往前追溯，直到找到一個非假日且非週末的日期
            while (previousDay > DateOnly.MinValue
                && (previousDay.DayOfWeek == DayOfWeek.Saturday
                || previousDay.DayOfWeek == DayOfWeek.Sunday
                || IsHoliday(previousDay))) // 使用 IsHoliday 方法判斷
            {
                previousDay = previousDay.AddDays(-1);
            }

            return previousDay;
        }

        private static string ResolvePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

            if (Path.IsPathRooted(filePath))
                return filePath;

            var repoRoot = TryFindRepoRoot(AppContext.BaseDirectory) ?? TryFindRepoRoot(Directory.GetCurrentDirectory());
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                // appsettings lives under MuNTrade.Cli, so Data/... paths should resolve from there first.
                var cliBasePath = Path.GetFullPath(Path.Combine(repoRoot, "MuNTrade.Cli", filePath));
                if (File.Exists(cliBasePath))
                    return cliBasePath;

                var repoBasePath = Path.GetFullPath(Path.Combine(repoRoot, filePath));
                if (File.Exists(repoBasePath))
                    return repoBasePath;
            }

            var currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), filePath));
            if (File.Exists(currentDirectoryPath))
                return currentDirectoryPath;

            var appBasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, filePath));
            if (File.Exists(appBasePath))
                return appBasePath;

            if (!string.IsNullOrWhiteSpace(repoRoot))
                return Path.GetFullPath(Path.Combine(repoRoot, "MuNTrade.Cli", filePath));

            return currentDirectoryPath;
        }

        private static string? TryFindRepoRoot(string startPath)
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MuNTrade.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            return null;
        }
    }
}
