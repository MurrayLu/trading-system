using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Core.Extensions;
using MuNTrade.Infrastructure.Extensions;

internal static class DemoCli
{
    private const string GeneratorCommand = "dotnet run --project tools/MuNTrade.SampleData -c Release";

    public static async Task<int> RunAsync(string[] args)
    {
        string? managedOutputRoot = null;
        try
        {
            var root = FindRepositoryRoot();
            var parsed = CliArguments.Parse(args);
            if (parsed.Command == "help")
            {
                CliArguments.PrintUsage();
                return 0;
            }

            if (parsed.Command == "report")
            {
                var input = ResolveFromRoot(parsed.In!, root);
                var site = ResolveFromRoot(parsed.Site!, root);
                var reportPath = StaticReportGenerator.Generate(input, site);
                Console.WriteLine($"靜態報表已產生：{reportPath}");
                return 0;
            }

            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.demo.json");
            if (!File.Exists(settingsPath))
                settingsPath = Path.Combine(root, "src", "MuNTrade.Demo.Cli", "appsettings.demo.json");

            var values = LoadJsonValues(settingsPath);
            ApplyEnvironmentOverrides(values);
            values["ApplicationSettings:AppMode"] = "Backtest";
            if (parsed.Command == "backtest") BacktestRunner.ApplyCommandOverrides(values, parsed);
            ResolveConfiguredPaths(values, root);

            if (parsed.Command == "backtest")
            {
                var reportRoot = values.TryGetValue("BacktestSettings:ReportRoot", out var configuredRoot)
                    && !string.IsNullOrWhiteSpace(configuredRoot)
                    ? ResolveFromRoot(configuredRoot, root)
                    : Path.Combine(root, "out");
                values["BacktestSettings:ReportRoot"] = reportRoot;
                values["BacktestSettings:StrategyEventRoot"] = Path.Combine(reportRoot, "strategy-events");
                DemoOutputDirectory.Prepare(reportRoot);
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

            return await BacktestRunner.RunAsync(parsed, root, configuration, scope.ServiceProvider, registry, GeneratorCommand);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            CliArguments.PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"執行失敗：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (managedOutputRoot is not null)
            {
                try { DemoOutputDirectory.WriteManifest(managedOutputRoot); }
                catch (Exception ex) { Console.Error.WriteLine($"無法更新輸出目錄清單：{ex.Message}"); }
            }
        }
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
            foreach (var item in element.EnumerateArray()) FlattenJson(item, $"{prefix}:{index++}", values);
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

    private static void ResolveConfiguredPaths(IDictionary<string, string?> values, string root)
    {
        foreach (var key in new[]
        {
            "DataSourceSettings:TickDataBasePath", "DataSourceSettings:KLineJsonBasePath",
            "DataSourceSettings:HolidayCalendarPath", "DataSourceSettings:TurnoverCsvBasePath",
            "BacktestSettings:ReportRoot", "BacktestSettings:StrategyEventRoot"
        })
        {
            if (values.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path))
                values[key] = ResolveFromRoot(path, root);
        }
    }

    internal static string ResolveFromRoot(string path, string root)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, root);

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
}
