using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Core.Backtesting;
using MuNTrade.Core.Settings;

internal static class BacktestRunner
{
    public static async Task<int> RunAsync(
        CliArguments parsed,
        string root,
        IConfiguration configuration,
        IServiceProvider scopedServices,
        IStrategyRegistry registry,
        string generatorCommand)
    {
        var defaults = configuration.GetSection("BacktestSettings").Get<BacktestSettings>() ?? new BacktestSettings();
        var start = parsed.From ?? ParseDate(defaults.StartDate, "BacktestSettings:StartDate");
        var end = parsed.To ?? ParseDate(defaults.EndDate, "BacktestSettings:EndDate");
        if (start > end) throw new ArgumentException("--from 不可晚於 --to。");

        var symbols = parsed.Symbol is null
            ? defaults.StockIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { parsed.Symbol };
        if (symbols.Count == 0)
            throw new ArgumentException("設定 BacktestSettings.StockIds 為空，請提供 --symbol 或預設商品清單。");

        var strategyId = parsed.Strategy ?? defaults.StrategyTemplateId;
        var template = registry.Templates.FirstOrDefault(x => string.Equals(x.TemplateId.Value, strategyId, StringComparison.Ordinal));
        if (template is null)
        {
            Console.Error.WriteLine($"找不到策略範本：{strategyId}");
            Console.Error.WriteLine("可用策略範本：");
            foreach (var item in registry.Templates.OrderBy(x => x.TemplateId.Value, StringComparer.Ordinal))
                Console.Error.WriteLine($"  {item.TemplateId.Value}");
            return 2;
        }

        var data = configuration.GetSection("DataSourceSettings").Get<DataSourceSettings>() ?? new DataSourceSettings();
        if (!HasRequiredSampleFiles(root, data, symbols, start, end))
        {
            Console.Error.WriteLine("找不到指定日期與商品的樣本 tick 或 K 線資料。");
            Console.Error.WriteLine($"請先執行樣本資料產生器：{generatorCommand}");
            return 2;
        }

        ApplyDemoTradingCosts(scopedServices, defaults.Execution);
        var reportRoot = Path.GetFullPath(defaults.ReportRoot, root);
        var tickRoot = DemoCli.ResolveFromRoot(data.TickDataBasePath, root);
        var tickSource = Path.GetRelativePath(root, tickRoot).Replace(Path.DirectorySeparatorChar, '/');
        if (tickSource == ".." || tickSource.StartsWith("../", StringComparison.Ordinal)) tickSource = "external configured tick source";
        var tickCount = CountTicks(tickRoot, symbols, start, end, DemoCli.ResolveFromRoot(data.HolidayCalendarPath, root));
        var metadata = new BacktestMetadata(start, end, symbols.OrderBy(x => x, StringComparer.Ordinal).ToArray(), strategyId, tickRoot, tickSource, tickCount);
        File.WriteAllText(Path.Combine(reportRoot, "backtest_metadata.json"), JsonSerializer.Serialize(metadata), new System.Text.UTF8Encoding(false));
        var request = new BacktestRunRequest
        {
            ReportMode = "C2",
            Stage = "Explore",
            TimeMode = "Range",
            StartDate = start,
            EndDate = end,
            UniverseMode = parsed.Symbol is null ? "ManualList" : "SingleSymbol",
            Symbol = parsed.Symbol,
            ManualList = symbols,
            StrategyTemplateId = strategyId,
            AccountKey = defaults.AccountKey,
            ReportRoot = reportRoot,
            SkipMissingData = false,
            Risk = defaults.Risk,
            Execution = defaults.Execution
        };

        Console.WriteLine($"策略：{strategyId}");
        Console.WriteLine($"日期：{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}");
        Console.WriteLine($"商品：{string.Join(", ", symbols)}");
        Console.WriteLine("回測執行中…");

        var host = scopedServices.GetRequiredService<BacktestHost>();
        var result = await host.RunAsync(request, new InlineProgress<BacktestProgress>(ReportProgress));
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) Console.Error.WriteLine(error);
            return 1;
        }

        var journalCounts = ReadJournalCounts(result.EventJournalPaths);
        var summaries = result.Summaries.Where(x => !x.Skipped).ToArray();
        var bySymbol = summaries.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.Ordinal);
        var totalFills = 0;
        var totalRoundTrips = 0;
        decimal totalGrossPnL = 0m, totalCosts = 0m, totalNetPnL = 0m;

        Console.WriteLine();
        Console.WriteLine("回測摘要 Backtest Summary");
        Console.WriteLine("商品 Symbol\t成交 Fills\t完成交易 Round trips\t毛損益 Gross PnL\t交易成本 Costs\t淨損益 Net PnL");
        foreach (var symbolGroup in bySymbol)
        {
            var stats = journalCounts.Where(x => string.Equals(x.Key.Symbol, symbolGroup.Key, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .Aggregate(new JournalCounts(), (sum, item) => new JournalCounts(
                    sum.Fills + item.Fills,
                    sum.RoundTrips + item.RoundTrips,
                    sum.GrossPnL + item.GrossPnL,
                    sum.Costs + item.Costs,
                    sum.NetPnL + item.NetPnL));
            totalFills += stats.Fills;
            totalRoundTrips += stats.RoundTrips;
            totalGrossPnL += stats.GrossPnL;
            totalCosts += stats.Costs;
            totalNetPnL += stats.NetPnL;
            Console.WriteLine($"{symbolGroup.Key}\t{stats.Fills}\t{stats.RoundTrips}\t{Format(stats.GrossPnL)}\t{Format(stats.Costs)}\t{Format(stats.NetPnL)}");
        }

        Console.WriteLine($"總計 Total\t{totalFills}\t{totalRoundTrips}\t{Format(totalGrossPnL)}\t{Format(totalCosts)}\t{Format(totalNetPnL)}");
        Console.WriteLine($"報表目錄 Output：{reportRoot}");
        Console.WriteLine($"摘要檔 Summary：{result.SummaryPath}");
        if (totalFills == 0)
        {
            Console.Error.WriteLine("回測完成但成交數為 0；請檢查價格路徑、策略範本與樣本資料。");
            return 1;
        }
        return 0;
    }

    public static void ApplyCommandOverrides(IDictionary<string, string?> values, CliArguments parsed)
    {
        if (parsed.From.HasValue) values["BacktestSettings:StartDate"] = parsed.From.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (parsed.To.HasValue) values["BacktestSettings:EndDate"] = parsed.To.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (parsed.Strategy is not null) values["BacktestSettings:StrategyTemplateId"] = parsed.Strategy;
        if (parsed.Out is not null) values["BacktestSettings:ReportRoot"] = parsed.Out;
        if (parsed.Symbol is not null)
        {
            values["BacktestSettings:UniverseMode"] = "SingleSymbol";
            values["BacktestSettings:StockIds:0"] = parsed.Symbol;
            for (var i = 1; i < 5; i++) values.Remove($"BacktestSettings:StockIds:{i}");
        }
        else values["BacktestSettings:UniverseMode"] = "ManualList";
    }

    private static bool HasRequiredSampleFiles(string root, DataSourceSettings data, IReadOnlyList<string> symbols, DateOnly from, DateOnly to)
    {
        var tickRoot = DemoCli.ResolveFromRoot(data.TickDataBasePath, root);
        var klineRoot = DemoCli.ResolveFromRoot(data.KLineJsonBasePath, root);
        var holidayPath = DemoCli.ResolveFromRoot(data.HolidayCalendarPath, root);
        var holidays = File.Exists(holidayPath)
            ? File.ReadLines(holidayPath).Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#'))
                .Select(x => DateOnly.TryParse(x.Trim(), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null)
                .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet()
            : new HashSet<DateOnly>();

        foreach (var symbol in symbols)
        {
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(day)) continue;
                var tickPath = Path.Combine(tickRoot, symbol, $"{day:yyyyMMdd}_D_{symbol}.csv.gz");
                if (!File.Exists(tickPath)) return false;
            }

            var symbolKlineRoot = Path.Combine(klineRoot, symbol);
            if (!Directory.Exists(symbolKlineRoot) || !Directory.EnumerateFiles(symbolKlineRoot, "1 分 K 線_*.json").Any())
                return false;
        }
        return true;
    }

    private static long CountTicks(string tickRoot, IReadOnlyList<string> symbols, DateOnly from, DateOnly to, string holidayPath)
    {
        var holidays = File.Exists(holidayPath)
            ? File.ReadLines(holidayPath).Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#'))
                .Select(x => DateOnly.TryParse(x.Trim(), CultureInfo.InvariantCulture, out var date) ? date : (DateOnly?)null)
                .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet()
            : new HashSet<DateOnly>();
        long total = 0;
        foreach (var symbol in symbols)
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(date)) continue;
            var path = Path.Combine(tickRoot, symbol, $"{date:yyyyMMdd}_D_{symbol}.csv.gz");
            using var compressed = File.OpenRead(path);
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            _ = reader.ReadLine();
            while (reader.ReadLine() is not null) total++;
        }
        return total;
    }

    private static Dictionary<(DateOnly Date, string Symbol), JournalCounts> ReadJournalCounts(IEnumerable<string> paths)
    {
        var result = new Dictionary<(DateOnly, string), JournalCounts>();
        foreach (var path in paths)
        {
            var dateText = Directory.GetParent(path)?.Name;
            if (!DateOnly.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var symbol = Path.GetFileNameWithoutExtension(path);
            var fills = 0;
            var rounds = 0;
            decimal gross = 0m, costs = 0m, net = 0m;
            foreach (var line in File.ReadLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = ReadString(GetProperty(root, "Type"));
                var evt = GetProperty(root, "Event");
                if (string.Equals(type, "OrderFillEvent", StringComparison.Ordinal)) fills++;
                if (!string.Equals(type, "CompletedTrade", StringComparison.Ordinal)) continue;
                rounds++;
                gross += GetDecimal(evt, "GrossPnL");
                costs += GetDecimal(evt, "Commission") + GetDecimal(evt, "Tax");
                net += GetDecimal(evt, "NetPnL");
            }
            result[(date, symbol)] = new JournalCounts(fills, rounds, gross, costs, net);
        }
        return result;
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string? ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static decimal GetDecimal(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value) ? value : 0m;
    }

    private static DateOnly ParseDate(string? value, string key)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new ArgumentException($"{key} 必須使用 yyyy-MM-dd 格式。");
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static void ReportProgress(BacktestProgress progress)
    {
        if (progress.Phase is BacktestProgressPhase.Loading or BacktestProgressPhase.Done)
            Console.WriteLine($"進度：{progress.Phase} {progress.DayIndex}/{progress.DayTotal}");
    }

    private static void ApplyDemoTradingCosts(IServiceProvider scopedServices, BacktestExecutionOptions execution)
    {
        var rules = scopedServices.GetRequiredService<ITradeRulesProvider>().Current;
        if (execution.Fees > 0m) rules.Stock.FeeDiscount = execution.Fees / 0.001425m;
        if (execution.Tax > 0m) rules.Stock.DayTradeTaxRate = execution.Tax;
    }

    private readonly record struct JournalCounts(int Fills = 0, int RoundTrips = 0, decimal GrossPnL = 0m, decimal Costs = 0m, decimal NetPnL = 0m);
    private sealed record BacktestMetadata(DateOnly StartDate, DateOnly EndDate, string[] Symbols, string StrategyTemplateId, string TickDataBasePath, string TickSource, long TotalTicks);
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
