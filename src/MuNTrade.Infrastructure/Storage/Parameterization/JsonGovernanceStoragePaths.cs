using Microsoft.Extensions.Configuration;

namespace MuNTrade.Infrastructure.Storage.Parameterization;

internal static class JsonGovernanceStoragePaths
{
    public static string Resolve(IConfiguration configuration, string key, string fallbackRelativePath)
    {
        var configured = configuration[key];
        var path = string.IsNullOrWhiteSpace(configured) ? fallbackRelativePath : configured;
        if (Path.IsPathRooted(path))
            return path;

        var appBasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(appBasePath) || Directory.Exists(appBasePath))
            return appBasePath;

        var currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (File.Exists(currentDirectoryPath) || Directory.Exists(currentDirectoryPath))
            return currentDirectoryPath;

        var repoRoot = TryFindRepoRoot(AppContext.BaseDirectory) ?? TryFindRepoRoot(Directory.GetCurrentDirectory());
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return Path.GetFullPath(Path.Combine(repoRoot, path));

        return currentDirectoryPath;
    }

    public static string ResolveLiveEnabledWhitelist(IConfiguration configuration) =>
        Resolve(
            configuration,
            "DaytradeGovernance:WhitelistLiveEnabledPath",
            Path.Combine("Data", "Experimentation", "parameter-whitelist.live-enabled.daytrade.json"));

    public static string ResolveResearchOnlyWhitelist(IConfiguration configuration) =>
        Resolve(
            configuration,
            "DaytradeGovernance:WhitelistResearchOnlyPath",
            Path.Combine("Data", "Experimentation", "parameter-whitelist.research-only.daytrade.json"));

    public static string ResolveLayer1SeedTable(IConfiguration configuration) =>
        Resolve(
            configuration,
            "DaytradeGovernance:SeedPath",
            Path.Combine("Data", "Experimentation", "parameter-seed.layer1.daytrade.json"));

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
