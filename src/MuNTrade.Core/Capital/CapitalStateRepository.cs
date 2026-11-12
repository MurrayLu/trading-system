using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Capital;
using System.Linq;

namespace MuNTrade.Core.Capital
{
    public class CapitalStateRepository : ICapitalStateRepository
    {
        private static string FilePath =>
            Path.Combine(CapitalConfigService.GetLiveAccountsPath(), "CapitalState.json");

        public async Task<CapitalState> LoadAsync()
        {
            var state = new CapitalState();
            if (File.Exists(FilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(FilePath);
                    state = JsonSerializer.Deserialize<CapitalState>(json) ?? new CapitalState();
                }
                catch
                {
                    // Fallback if file is corrupted
                }
            }

            if (state.Accounts == null) state.Accounts = new System.Collections.Generic.List<AccountProfile>();

            // [Auto-Seed] If no LIVE accounts exist, seed them from expected credentials
            bool hasLive = state.Accounts.Any(a => string.Equals(a.GatewayMode, "LIVE", System.StringComparison.OrdinalIgnoreCase));
            if (!hasLive)
            {
                var creds = CapitalConfigService.LoadCredentials();
                if (creds != null && creds.Accounts != null)
                {
                    foreach (var acc in creds.Accounts)
                    {
                        if (acc.Broker != null && 
                            (string.IsNullOrEmpty(acc.Broker.Mode) || acc.Broker.Mode.Equals("Real", System.StringComparison.OrdinalIgnoreCase)))
                        {
                            state.Accounts.Add(new AccountProfile
                            {
                                Id = acc.AccountKey,
                                Name = string.IsNullOrWhiteSpace(acc.DisplayName) ? acc.AccountKey : acc.DisplayName,
                                Type = "Real",
                                GatewayMode = "LIVE",
                                InitialEquity = 3000000m, // Default starting simulation assumption if missing
                                RealizedPnL = 0,
                                UsedQuota = 0
                            });
                        }
                    }
                }

                if (state.Accounts.Any() && string.IsNullOrEmpty(state.ActiveAccountId))
                {
                    state.ActiveAccountId = state.Accounts.First().Id;
                }

                // Fire & forget save to persist the seed
                _ = SaveAsync(state);
            }

            return state;
        }

        public async Task SaveAsync(CapitalState state)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(FilePath, json);
            }
            catch
            {
                // Handle save error (log or ignore)
            }
        }
    }
}

