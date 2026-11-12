using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Core.Backtesting;
using MuNTrade.Core.Extensions;
using MuNTrade.Core.Settings;
using MuNTrade.Infrastructure.Extensions;

return await DemoCli.RunAsync(args);

public static class DemoCli
{
    private const string GeneratorCommand = "dotnet run --project tools/MuNTrade.SampleData -c Release";
    private const string OutputMarkerName = ".muntrade-demo-output";
    private const string OutputManifestName = ".muntrade-demo-manifest.json";
    private const string OutputMarkerContent = "MuNTrade.Demo.Cli output directory v1";

    public static async Task<int> RunAsync(string[] args)
    {
        string? managedOutputRoot = null;
        try
        {
            var root = FindRepositoryRoot();
            var parsed = ParseArguments(args);
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.demo.json");
            if (!File.Exists(settingsPath))
                settingsPath = Path.Combine(root, "src", "MuNTrade.Demo.Cli", "appsettings.demo.json");

            var values = LoadJsonValues(settingsPath);
            ApplyEnvironmentOverrides(values);
            values["ApplicationSettings:AppMode"] = "Backtest";

            if (parsed.Command == "help")
            {
                PrintUsage();
                return 0;
            }

            if (parsed.Command == "backtest")
                ApplyCommandOverrides(values, parsed);

            ResolveConfiguredPaths(values, root);
            if (parsed.Command == "backtest")
            {
                var reportRoot = values.TryGetValue("BacktestSettings:ReportRoot", out var configuredRoot)
                    && !string.IsNullOrWhiteSpace(configuredRoot)
                    ? ResolveFromRoot(configuredRoot, root)
                    : Path.Combine(root, "Reports");
                values["BacktestSettings:ReportRoot"] = reportRoot;
                values["BacktestSettings:StrategyEventRoot"] = Path.Combine(reportRoot, "strategy-events");
                PrepareOutputDirectory(reportRoot);
                managedOutputRoot = reportRoot;

                // Core's legacy Config paths are relative to the working directory.
                // Keep all of them under this marked, managed output directory.
                Directory.SetCurrentDirectory(reportRoot);
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            using var provider = BuildServices(configuration);
            using var scope = provider.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStrategyRegistry>();

            if (parsed.Command == "list-strategies")
            {
                foreach (var template in registry.Templates.OrderBy(x => x.TemplateId.Value, StringComparer.Ordinal))
                    Console.WriteLine($"{template.TemplateId.Value}\t{template.Version}");
                return 0;
            }

            return await RunBacktestAsync(parsed, root, configuration, scope.ServiceProvider, registry);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"回測失敗：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (managedOutputRoot is not null)
            {
                try
                {
                    WriteOutputManifest(managedOutputRoot);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"無法更新輸出目錄清單：{ex.Message}");
                }
            }
        }
    }

    private static async Task<int> RunBacktestAsync(
        ParsedArguments parsed,
        string root,
        IConfiguration configuration,
        IServiceProvider scopedServices,
        IStrategyRegistry registry)
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
            Console.Error.WriteLine($"請先執行樣本資料產生器：{GeneratorCommand}");
            return 2;
        }

        ApplyDemoTradingCosts(scopedServices, defaults.Execution);

        var reportRoot = Path.GetFullPath(defaults.ReportRoot, root);
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
        var bySymbol = summaries.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.Ordinal);
        var totalOrders = 0;
        var totalFills = 0;
        decimal totalGrossPnL = 0m;
        decimal totalNetPnL = 0m;

        Console.WriteLine();
        Console.WriteLine("回測摘要");
        Console.WriteLine("商品 Symbol\t委託 Orders\t成交 Fills\t毛損益 Gross PnL\t淨損益 Net PnL");
        foreach (var symbolGroup in bySymbol)
        {
            var stats = journalCounts.Where(x => string.Equals(x.Key.Symbol, symbolGroup.Key, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .Aggregate(new JournalCounts(), (sum, item) => new JournalCounts(
                    sum.Orders + item.Orders,
                    sum.Fills + item.Fills,
                    sum.GrossPnL + item.GrossPnL,
                    sum.NetPnL + item.NetPnL));
            var netPnL = symbolGroup.Sum(x => x.NetPnL);
            totalOrders += stats.Orders;
            totalFills += stats.Fills;
            totalGrossPnL += stats.GrossPnL;
            totalNetPnL += netPnL;
            Console.WriteLine($"{symbolGroup.Key}\t{stats.Orders}\t{stats.Fills}\t{stats.GrossPnL.ToString("0.##", CultureInfo.InvariantCulture)}\t{netPnL.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        Console.WriteLine($"總計 Total\t{totalOrders}\t{totalFills}\t{totalGrossPnL.ToString("0.##", CultureInfo.InvariantCulture)}\t{totalNetPnL.ToString("0.##", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"報表目錄：{reportRoot}");
        Console.WriteLine($"摘要檔：{result.SummaryPath}");

        if (totalFills == 0)
        {
            Console.Error.WriteLine("回測完成但成交數為 0；請檢查價格路徑、策略範本與樣本資料。");
            return 1;
        }

        return 0;
    }

    private static void ApplyDemoTradingCosts(IServiceProvider scopedServices, BacktestExecutionOptions execution)
    {
        var rules = scopedServices.GetRequiredService<ITradeRulesProvider>().Current;

        // The ledger's commission is the TWSE base rate multiplied by FeeDiscount.
        // Convert the configured rate back to that existing multiplier without changing ledger accounting.
        if (execution.Fees > 0m)
            rules.Stock.FeeDiscount = execution.Fees / 0.001425m;
        if (execution.Tax > 0m)
            rules.Stock.DayTradeTaxRate = execution.Tax;
    }

    private static ServiceProvider BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddOptions();
        services.AddMuNTradeCoreBase(configuration, AppMode.Backtest);
        services.AddTickPipeline(AppMode.Backtest);
        services.AddBarPipelines(AppMode.Backtest);
        services.AddBacktestingServices();
        services.AddMuNTradeInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static bool HasRequiredSampleFiles(
        string root,
        DataSourceSettings data,
        IReadOnlyList<string> symbols,
        DateOnly from,
        DateOnly to)
    {
        var tickRoot = ResolveFromRoot(data.TickDataBasePath, root);
        var klineRoot = ResolveFromRoot(data.KLineJsonBasePath, root);
        var holidayPath = ResolveFromRoot(data.HolidayCalendarPath, root);
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

            if (!Directory.Exists(Path.Combine(klineRoot, symbol)) ||
                !Directory.EnumerateFiles(Path.Combine(klineRoot, symbol), "1 分 K 線_*.json").Any())
                return false;
        }

        return true;
    }

    private static Dictionary<(DateOnly Date, string Symbol), JournalCounts> ReadJournalCounts(IEnumerable<string> paths)
    {
        var result = new Dictionary<(DateOnly, string), JournalCounts>();
        foreach (var path in paths)
        {
            var dateText = Directory.GetParent(path)?.Name;
            if (!DateOnly.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            var symbol = Path.GetFileNameWithoutExtension(path);
            var orders = 0;
            var fills = 0;
            decimal grossPnL = 0m;
            decimal netPnL = 0m;
            foreach (var line in File.ReadLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = GetProperty(root, "Type").GetString();
                var evt = GetProperty(root, "Event");
                if (string.Equals(type, "OrderFillEvent", StringComparison.Ordinal)) fills++;
                if (string.Equals(type, "CompletedTrade", StringComparison.Ordinal))
                {
                    grossPnL += GetDecimal(evt, "GrossPnL");
                    netPnL += GetDecimal(evt, "NetPnL");
                }
                if (string.Equals(type, "StrategyPacketEmittedEvent", StringComparison.Ordinal) &&
                    string.Equals(GetProperty(evt, "ActionCode").GetString(), "PROBE_INTENT", StringComparison.Ordinal))
                    orders++;
            }

            result[(date, symbol)] = new JournalCounts(orders, fills, grossPnL, netPnL);
        }

        return result;
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return default;
    }

    private static decimal GetDecimal(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value) ? value : 0m;
    }

    private static Dictionary<string, string?> LoadJsonValues(string settingsPath)
    {
        if (!File.Exists(settingsPath)) throw new FileNotFoundException("找不到 CLI 設定檔。", settingsPath);
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenJson(document.RootElement, string.Empty, values);
        return values;
    }

    private static void FlattenJson(JsonElement element, string prefix, IDictionary<string, string?> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                FlattenJson(property.Value, string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}", values);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                FlattenJson(item, $"{prefix}:{index++}", values);
            return;
        }

        values[prefix] = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private static void ApplyEnvironmentOverrides(IDictionary<string, string?> values)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || !name.StartsWith("MUNTRADE_", StringComparison.OrdinalIgnoreCase)) continue;
            var key = name["MUNTRADE_".Length..].Replace("__", ":", StringComparison.Ordinal);
            if (key.Length > 0) values[key] = entry.Value?.ToString();
        }
    }

    private static void ApplyCommandOverrides(IDictionary<string, string?> values, ParsedArguments parsed)
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
        else
        {
            values["BacktestSettings:UniverseMode"] = "ManualList";
        }
    }

    private static void ResolveConfiguredPaths(IDictionary<string, string?> values, string root)
    {
        foreach (var key in new[]
        {
            "DataSourceSettings:TickDataBasePath",
            "DataSourceSettings:KLineJsonBasePath",
            "DataSourceSettings:HolidayCalendarPath",
            "DataSourceSettings:TurnoverCsvBasePath",
            "BacktestSettings:ReportRoot",
            "BacktestSettings:StrategyEventRoot"
        })
        {
            if (values.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path))
                values[key] = ResolveFromRoot(path, root);
        }
    }

    private static string ResolveFromRoot(string path, string root)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, root);

    private static void PrepareOutputDirectory(string outputRoot)
    {
        outputRoot = Path.GetFullPath(outputRoot);
        if (File.Exists(outputRoot))
            throw new ArgumentException($"輸出路徑不是目錄：{outputRoot}");

        Directory.CreateDirectory(outputRoot);
        var actualEntries = EnumerateOutputEntries(outputRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var markerEntry = $"F:{OutputMarkerName}";
        var manifestEntry = $"F:{OutputManifestName}";
        var markerPath = Path.Combine(outputRoot, OutputMarkerName);
        var manifestPath = Path.Combine(outputRoot, OutputManifestName);

        if (actualEntries.Count == 0)
        {
            File.WriteAllText(markerPath, OutputMarkerContent, new UTF8Encoding(false));
            return;
        }

        if (!File.Exists(markerPath) || !string.Equals(File.ReadAllText(markerPath), OutputMarkerContent, StringComparison.Ordinal))
            throw new ArgumentException($"輸出目錄非空且非本工具上次輸出，拒絕清理：{outputRoot}");

        if (!File.Exists(manifestPath))
        {
            if (actualEntries.SetEquals([markerEntry])) return;
            throw new ArgumentException($"輸出目錄缺少有效的本工具清單，拒絕清理：{outputRoot}");
        }

        string[] previousEntries;
        try
        {
            previousEntries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? [];
        }
        catch (JsonException)
        {
            throw new ArgumentException($"輸出目錄清單無法辨識，拒絕清理：{outputRoot}");
        }

        var allowedEntries = previousEntries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowedEntries.Add(markerEntry);
        allowedEntries.Add(manifestEntry);
        var unknownEntries = actualEntries.Except(allowedEntries, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknownEntries.Length > 0)
            throw new ArgumentException($"輸出目錄含有不屬於本工具的檔案或資料夾，拒絕清理：{outputRoot}");

        foreach (var entry in previousEntries.Where(actualEntries.Contains).Where(x => x.StartsWith("F:", StringComparison.Ordinal)))
            DeleteOutputFileWithRetry(ResolveOutputEntry(outputRoot, entry[2..]));

        foreach (var entry in previousEntries.Where(actualEntries.Contains).Where(x => x.StartsWith("D:", StringComparison.Ordinal))
                     .OrderByDescending(x => x.Length))
        {
            var directory = ResolveOutputEntry(outputRoot, entry[2..]);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }

        File.Delete(manifestPath);
    }

    private static void DeleteOutputFileWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }

    private static void WriteOutputManifest(string outputRoot)
    {
        outputRoot = Path.GetFullPath(outputRoot);
        var markerPath = Path.Combine(outputRoot, OutputMarkerName);
        if (!File.Exists(markerPath) || !string.Equals(File.ReadAllText(markerPath), OutputMarkerContent, StringComparison.Ordinal))
            return;

        var entries = EnumerateOutputEntries(outputRoot)
            .Where(x => !string.Equals(x, $"F:{OutputMarkerName}", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(x, $"F:{OutputManifestName}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        File.WriteAllText(Path.Combine(outputRoot, OutputManifestName), JsonSerializer.Serialize(entries), new UTF8Encoding(false));
    }

    private static IEnumerable<string> EnumerateOutputEntries(string outputRoot)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(outputRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"輸出目錄包含連結項目，拒絕清理：{path}");

            var relative = Path.GetRelativePath(outputRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var kind = Directory.Exists(path) ? "D:" : "F:";
            yield return kind + relative;
        }
    }

    private static string ResolveOutputEntry(string outputRoot, string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"輸出清單含有越界路徑，拒絕清理：{relative}");
        return fullPath;
    }

    private static DateOnly ParseDate(string? value, string key)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new ArgumentException($"{key} 必須使用 yyyy-MM-dd 格式。");
    }

    private static ParsedArguments ParseArguments(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return new ParsedArguments("help");
        var command = args[0];
        if (command == "list-strategies")
        {
            if (args.Length != 1) throw new ArgumentException("list-strategies 不接受額外參數。");
            return new ParsedArguments(command);
        }
        if (command != "backtest") throw new ArgumentException($"未知子命令：{command}");

        string? symbol = null;
        DateOnly? from = null;
        DateOnly? to = null;
        string? strategy = null;
        string? output = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 後面需要一個值。");
            var option = args[i++];
            var value = args[i];
            switch (option)
            {
                case "--symbol": symbol = value; break;
                case "--from": from = ParseDate(value, "--from"); break;
                case "--to": to = ParseDate(value, "--to"); break;
                case "--strategy": strategy = value; break;
                case "--out": output = value; break;
                default: throw new ArgumentException($"未知參數：{option}");
            }
        }

        return new ParsedArguments(command, symbol, from, to, strategy, output);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "MuNTrade.sln"))) return current.FullName;
                current = current.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }

    private static void ReportProgress(BacktestProgress progress)
    {
        if (progress.Phase is BacktestProgressPhase.Loading or BacktestProgressPhase.Done)
            Console.WriteLine($"進度：{progress.Phase} {progress.DayIndex}/{progress.DayTotal}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run --project src/MuNTrade.Demo.Cli -c Release -- list-strategies");
        Console.WriteLine("  dotnet run --project src/MuNTrade.Demo.Cli -c Release -- backtest [--symbol <id>] [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--strategy <templateId>] [--out <dir>]");
    }

    private sealed record ParsedArguments(
        string Command,
        string? Symbol = null,
        DateOnly? From = null,
        DateOnly? To = null,
        string? Strategy = null,
        string? Out = null);

    private readonly record struct JournalCounts(
        int Orders = 0,
        int Fills = 0,
        decimal GrossPnL = 0m,
        decimal NetPnL = 0m);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
