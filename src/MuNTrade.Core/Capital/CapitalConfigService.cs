using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using MuNTrade.Abstractions.Contracts.Time;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Core.Utils.Converters;
using System.Diagnostics;

namespace MuNTrade.Core.Capital
{
    public static class CapitalConfigService
    {
        // 預設路徑 (如果無法讀取 Config)
        private static string _basePath = Path.Combine(Directory.GetCurrentDirectory(), "Config");

        private static string SimAccountsPath => Path.Combine(_basePath, "SimAccounts");
        private static string LiveAccountsPath => Path.Combine(_basePath, "LiveAccounts");
        private static string TradeRulesPath => Path.Combine(_basePath, "TradeSettings");
        private static string RulesFile => Path.Combine(TradeRulesPath, "trade_rules.json");

        static CapitalConfigService()
        {
            // 嘗試從 appsettings.json 讀取路徑
            LoadPathsFromConfig();

            // 確保資料夾存在
            if (!Directory.Exists(ActualSimAccountsPath)) Directory.CreateDirectory(ActualSimAccountsPath);
            if (!Directory.Exists(ActualLiveAccountsPath)) Directory.CreateDirectory(ActualLiveAccountsPath);
            if (!Directory.Exists(TradeRulesPath)) Directory.CreateDirectory(TradeRulesPath);
        }

        private static void LoadPathsFromConfig()
        {
            var baseDir = Directory.GetCurrentDirectory();
            var primary = Path.Combine(baseDir, "appsettings.json");
            var backtest = Path.Combine(baseDir, "appsettings.backtest.json");

            // Prefer appsettings.json. If it doesn't exist or doesn't contain DataSourceSettings, fallback to backtest.
            if (TryLoadPathsFromConfig(primary)) return;
            TryLoadPathsFromConfig(backtest);
        }

        private static string _customSimPath = string.Empty;
        private static string _customLivePath = string.Empty;
        private static string _customTradePath = string.Empty;
        private static string _customCredentialsPath = string.Empty;

        private static bool TryLoadPathsFromConfig(string settingsPath)
        {
            try
            {
                if (!File.Exists(settingsPath)) return false;

                var json = File.ReadAllText(settingsPath);
                var options = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                using var doc = JsonDocument.Parse(json, options);

                var loadedAny = false;
                if (doc.RootElement.TryGetProperty("DataSourceSettings", out var ds))
                {
                    // 讀取設定的路徑
                    if (ds.TryGetProperty("SimAccountDataPath", out var simPath))
                    {
                        var configuredPath = simPath.GetString();
                        if (!string.IsNullOrWhiteSpace(configuredPath))
                        {
                            var resolved = ResolveConfigPath(settingsPath, configuredPath);
                            if (!string.IsNullOrWhiteSpace(resolved))
                                _customSimPath = resolved;
                            if (Path.IsPathRooted(configuredPath))
                                _customSimPath = configuredPath;
                            loadedAny = true;
                        }
                    }

                    if (ds.TryGetProperty("LiveAccountDataPath", out var livePath) && !string.IsNullOrWhiteSpace(livePath.GetString()))
                    {
                        var resolved = ResolveConfigPath(settingsPath, livePath.GetString());
                        if (!string.IsNullOrWhiteSpace(resolved))
                            _customLivePath = resolved;
                        loadedAny = true;
                    }

                    if (ds.TryGetProperty("TradeRulesDataPath", out var tradePath) && !string.IsNullOrWhiteSpace(tradePath.GetString()))
                    {
                        var resolved = ResolveConfigPath(settingsPath, tradePath.GetString());
                        if (!string.IsNullOrWhiteSpace(resolved))
                            _customTradePath = resolved;
                        loadedAny = true;
                    }
                }

                // Check ExternalFiles for Credentials
                if (doc.RootElement.TryGetProperty("ExternalFiles", out var ef))
                {
                    if (ef.TryGetProperty("CapitalCredentialsPath", out var ccp))
                    {
                        var credentialsPath = ccp.GetString();
                        if (!string.IsNullOrWhiteSpace(credentialsPath))
                        {
                            _customCredentialsPath = credentialsPath;
                            loadedAny = true;
                        }
                    }
                }

                return loadedAny;
            }
            catch
            {
                // Ignore config load errors, fallback to default
                return false;
            }
        }

        // 改寫路徑屬性優先使用 Config 設定
        private static string ActualSimAccountsPath => !string.IsNullOrEmpty(_customSimPath) ? _customSimPath : SimAccountsPath;
        private static string ActualLiveAccountsPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_customLivePath))
                    return _customLivePath;

                if (!string.IsNullOrEmpty(_customSimPath))
                {
                    var parent = Path.GetDirectoryName(_customSimPath);
                    if (!string.IsNullOrEmpty(parent))
                        return Path.Combine(parent, "LiveAccounts");
                }

                return LiveAccountsPath;
            }
        }
        private static string ActualRulesFile => !string.IsNullOrEmpty(_customTradePath)
            ? Path.Combine(_customTradePath, "trade_rules.json")
            : RulesFile;

        private static readonly JsonSerializerOptions SimAccountJsonOptions = CreateSimAccountJsonOptions();

        private static JsonSerializerOptions CreateSimAccountJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new DateOnlyJsonConverter());
            return options;
        }

        public static string GetSimAccountsPath() => ActualSimAccountsPath;
        public static string GetLiveAccountsPath() => ActualLiveAccountsPath;

        private static string? ResolveConfigPath(string settingsPath, string? pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue)) return null;
            if (Path.IsPathRooted(pathValue)) return pathValue;

            var baseDir = Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
            var repoRoot = TryFindRepoRoot(baseDir);
            var root = repoRoot ?? AppContext.BaseDirectory;
            return Path.GetFullPath(Path.Combine(root, pathValue));
        }

        private static string? TryFindRepoRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                var sln = Path.Combine(dir.FullName, "MuNTrade.sln");
                var git = Path.Combine(dir.FullName, ".git");
                if (File.Exists(sln) || Directory.Exists(git))
                    return dir.FullName;

                dir = dir.Parent;
            }

            return null;
        }


        // === 帳戶存取 ===
        public static List<SimAccountModel> LoadAllSimAccounts()
        {
            var list = new List<SimAccountModel>();
            if (!Directory.Exists(ActualSimAccountsPath)) Directory.CreateDirectory(ActualSimAccountsPath);

            var files = Directory.GetFiles(ActualSimAccountsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var acc = JsonSerializer.Deserialize<SimAccountModel>(json, SimAccountJsonOptions);
                    if (acc != null) list.Add(acc);
                }
                catch { /* Log Error */ }
            }
            var active = list.FirstOrDefault(a => a.IsActive) ?? list.FirstOrDefault();
            if (active != null)
            {
                //Debug.WriteLine($"[E2E][IO] LoadAllSimAccounts count={list.Count} active={active.Name} lastUpdated={active.LastUpdated:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                //Debug.WriteLine("[E2E][IO] LoadAllSimAccounts count=0 active=none");
            }
            return list;
        }

        public static void SaveSimAccount(SimAccountModel acc, IClock clock)
        {
            acc.LastUpdated = clock.Now;
            // 保存過一次後就不是 FirstSetup
            // (ViewModel 應該控制邏輯，這裡只是純 IO，但為了保險可以加)

            if (!Directory.Exists(ActualSimAccountsPath)) Directory.CreateDirectory(ActualSimAccountsPath);

            string path = Path.Combine(ActualSimAccountsPath, $"{acc.Id}.json");
            string json = JsonSerializer.Serialize(acc, SimAccountJsonOptions);
            File.WriteAllText(path, json);
        }

        public static void DeleteSimAccount(string id)
        {
            string path = Path.Combine(ActualSimAccountsPath, $"{id}.json");
            if (File.Exists(path)) File.Delete(path);
        }

        public static List<AccountProfile> LoadAllLiveAccounts()
        {
            var list = new List<AccountProfile>();
            if (!Directory.Exists(ActualLiveAccountsPath)) Directory.CreateDirectory(ActualLiveAccountsPath);

            var files = Directory.GetFiles(ActualLiveAccountsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var acc = JsonSerializer.Deserialize<AccountProfile>(json);
                    if (acc != null) list.Add(acc);
                }
                catch { /* Log Error */ }
            }
            return list;
        }

        public static void SaveLiveAccount(AccountProfile acc)
        {
            if (!Directory.Exists(ActualLiveAccountsPath)) Directory.CreateDirectory(ActualLiveAccountsPath);

            string path = Path.Combine(ActualLiveAccountsPath, $"{acc.Id}.json");
            string json = JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static void DeleteLiveAccount(string id)
        {
            string path = Path.Combine(ActualLiveAccountsPath, $"{id}.json");
            if (File.Exists(path)) File.Delete(path);
        }

        // === 交易規則存取 ===
        public static TradeRulesConfig LoadTradeRules()
        {
            if (!File.Exists(ActualRulesFile)) return new TradeRulesConfig(); // 回傳預設值
            try
            {
                var json = File.ReadAllText(ActualRulesFile);
                return JsonSerializer.Deserialize<TradeRulesConfig>(json) ?? new TradeRulesConfig();
            }
            catch { return new TradeRulesConfig(); }
        }

        public static void SaveTradeRules(TradeRulesConfig rules)
        {
            var dir = Path.GetDirectoryName(ActualRulesFile) ?? TradeRulesPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ActualRulesFile, json);
        }

        // ==========================================
        // V2 Credentials Support (AccountKey Unity)
        // ==========================================

        private static string CredentialsFile => !string.IsNullOrEmpty(_customCredentialsPath)
            ? _customCredentialsPath
            : Path.Combine(_basePath, "capital.credentials.json");
        private static V2Credentials? _cachedCredentials;
        private static readonly object _credLock = new();

        public static void ConfigureBasePath(string path)
        {
            _basePath = path;
        }

        public static V2Credentials LoadCredentials()
        {
            lock (_credLock)
            {
                if (_cachedCredentials != null) return _cachedCredentials;

                if (!File.Exists(CredentialsFile))
                {
                    // Fallback / Initial Template
                    _cachedCredentials = new V2Credentials
                    {
                        SchemaVersion = 2,
                        Provider = "SKCOM",
                        Environment = "Production",
                        Accounts = new List<V2Account>
                        {
                            new V2Account { AccountKey = "Sim:Paper:All:Default", DisplayName = "Paper Trading", Broker = new V2Broker { Mode = "Paper" }, Routing = new V2Routing { AllowOrder = false } }
                        }
                    };
                    return _cachedCredentials;
                }

                try
                {
                    var json = File.ReadAllText(CredentialsFile);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _cachedCredentials = JsonSerializer.Deserialize<V2Credentials>(json, opts);
                }
                catch
                {
                    _cachedCredentials = new V2Credentials();
                }
                return _cachedCredentials ?? new V2Credentials();
            }
        }

        /// <summary>
        /// Reverse Lookup: Broker Account -> AccountKey
        /// </summary>
        public static string ResolveAccountKey(string brokerAccountNo)
        {
            if (string.IsNullOrWhiteSpace(brokerAccountNo)) return "Unknown";

            // 1. Normalize Input
            string inputNorm = brokerAccountNo.Trim().ToUpperInvariant();

            // [Debug] Explicit logging for troubleshooting
            System.Diagnostics.Debug.WriteLine($"[ResolveAccountKey] Input='{brokerAccountNo}' Norm='{inputNorm}'");

            var creds = LoadCredentials();
            foreach (var acc in creds.Accounts)
            {
                if (acc.Broker == null) continue;

                // 2. Normalize Config Values
                string stockNorm = (acc.Broker.StockAccountNo ?? "").Trim().ToUpperInvariant();
                string futureNorm = (acc.Broker.FuturesAccountNo ?? "").Trim().ToUpperInvariant();

                // [Debug] Trace comparison
                System.Diagnostics.Debug.WriteLine($"   > Check [{acc.AccountKey}] Stk='{stockNorm}' Fut='{futureNorm}'");

                // 3. Compare
                if (!string.IsNullOrEmpty(stockNorm) && stockNorm == inputNorm) return acc.AccountKey;
                if (!string.IsNullOrEmpty(futureNorm) && futureNorm == inputNorm) return acc.AccountKey;
            }

            System.Diagnostics.Debug.WriteLine($"[ResolveAccountKey] Failed to resolve '{inputNorm}'. Returns Unknown.");
            return "Unknown";
        }

        /// <summary>
        /// Forward Lookup: AccountKey -> Broker Details
        /// </summary>
        public static V2Account? GetAccountConfig(string accountKey)
        {
            var creds = LoadCredentials();
            return creds.Accounts.FirstOrDefault(a => string.Equals(a.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
        }

        //V2 DTOs ---
        public class V2Credentials
        {
            public int SchemaVersion { get; set; }
            public string Provider { get; set; } = "";
            public string Environment { get; set; } = "";
            public List<V2Account> Accounts { get; set; } = new();
            public V2Logging Logging { get; set; } = new();
        }

        public class V2Account
        {
            public string AccountKey { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public V2Broker Broker { get; set; } = new();
            public V2Routing Routing { get; set; } = new();
        }

        public class V2Broker
        {
            public string LoginId { get; set; } = "";
            public string StockAccountNo { get; set; } = "";
            public string FuturesAccountNo { get; set; } = "";
            public string? Mode { get; set; } = ""; // "Paper" or null (Real)
        }

        public class V2Routing
        {
            public string DefaultMarket { get; set; } = "";
            public bool AllowOrder { get; set; }
        }

        public class V2Logging
        {
            public bool MaskAccountsInInfo { get; set; } = true;
            public string MaskRule { get; set; } = "last4";
        }
    }
}

