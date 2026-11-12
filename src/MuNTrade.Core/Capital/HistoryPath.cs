using System;
using System.IO;

namespace MuNTrade.Core.Capital
{
    public static class HistoryPath
    {
        public static bool IsPaperAccountKey(string? accountKey)
        {
            if (string.IsNullOrWhiteSpace(accountKey)) return false;
            return accountKey.StartsWith("SIM", StringComparison.OrdinalIgnoreCase)
                || accountKey.StartsWith("PAPER", StringComparison.OrdinalIgnoreCase)
                || accountKey.StartsWith("SIM:", StringComparison.OrdinalIgnoreCase)
                || accountKey.StartsWith("PAPER:", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveBaseDir(string? accountKey)
        {
            var accountsPath = IsPaperAccountKey(accountKey)
                ? CapitalConfigService.GetSimAccountsPath()
                : CapitalConfigService.GetLiveAccountsPath();

            var baseDir = Path.GetDirectoryName(accountsPath);
            return string.IsNullOrEmpty(baseDir) ? accountsPath : baseDir;
        }

        public static string ResolveHistoryRoot(string? accountKey, bool ensureExists = true)
        {
            var baseDir = ResolveBaseDir(accountKey);
            var scope = IsPaperAccountKey(accountKey) ? "paper" : "live";
            var path = Path.Combine(baseDir, "History", scope);
            if (ensureExists) Directory.CreateDirectory(path);
            return path;
        }

        public static string ResolveLegacyHistoryRoot(string? accountKey, bool ensureExists = true)
        {
            var baseDir = ResolveBaseDir(accountKey);
            var path = Path.Combine(baseDir, "History");
            if (ensureExists) Directory.CreateDirectory(path);
            return path;
        }

        public static string ResolveStrategiesRoot(string? accountKey, bool ensureExists = true)
        {
            var root = ResolveHistoryRoot(accountKey, ensureExists);
            var path = Path.Combine(root, "strategies");
            if (ensureExists) Directory.CreateDirectory(path);
            return path;
        }

        public static string ResolvePositionsPath(string? accountKey)
        {
            var safe = SanitizeFileName(string.IsNullOrWhiteSpace(accountKey) ? "Unknown" : accountKey);
            var root = ResolveHistoryRoot(accountKey, ensureExists: true);
            return Path.Combine(root, $"positions_{safe}.json");
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}

