using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;

namespace MuNTrade.Abstractions.Domain.Capital
{
    // 對應 JSON 中的單一帳戶
    public class CapitalAccountModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "新帳戶";
        public string Type { get; set; } = "Simulated"; // Simulated or Real
        public string GatewayMode { get; set; } = "PAPER";  // PAPER or LIVE
        public decimal InitialEquity { get; set; } = 100000;
        public decimal RealizedPnL { get; set; } = 0;
        public decimal UsedQuota { get; set; } = 0;
    }

    // 對應整個 JSON 檔案
    public class CapitalStateData
    {
        public List<CapitalAccountModel> Accounts { get; set; } = new();
        public string ActiveAccountId { get; set; } = "";
    }

    // 簡單的服務來讀寫 JSON (實際專案建議透過 DI 注入)
    public static class CapitalStateService
    {
        private const string FilePath = "CapitalState.json";

        public static CapitalStateData Load()
        {
            if (!File.Exists(FilePath)) return new CapitalStateData();
            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<CapitalStateData>(json) ?? new CapitalStateData();
            }
            catch
            {
                // 若解析失敗，回傳預設值
                return new CapitalStateData();
            }
        }

        public static void Save(CapitalStateData data)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(FilePath, json);
        }
    }
}

