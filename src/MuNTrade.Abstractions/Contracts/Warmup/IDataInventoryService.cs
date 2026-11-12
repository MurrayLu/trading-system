// MuNTrade.Abstractions/Interfaces/Services/IDataInventoryService.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Warmup
{
    /// <summary>每個 timeframe 的已覆蓋「日」區間（以日為單位做 union）。</summary>
    public sealed class CoverageInfo
    {
        public DateOnly? From { get; set; }  // 允許 null，避免 0001-01-01
        public DateOnly? To { get; set; }    // 允許 null
        // 僅在 downloader 明確確認「更早請求區間沒有資料」後才可設定。
        public DateOnly? FirstTradableDate { get; set; }
        public bool IsClosed { get; set; }
        public string Timeframe { get; set; } = "";
    }

    public interface IDataInventoryService
    {
        Task<CoverageInfo?> GetCoverageAsync(string symbol, string timeframeKey, CancellationToken ct = default);
        Task SaveCoverageAsync(string symbol, string timeframeKey, CoverageInfo info, CancellationToken ct = default);

        /// <summary>掃描磁碟上 prefix_{from}_{to}.json，讀內容建立 coverage（只看內容，不看檔名）。</summary>
        Task<CoverageInfo> RescanFromDiskAsync(string symbol, string timeframeKey, CancellationToken ct = default);

        /// <summary>合併一段 [from..to] 到 coverage 並落地。</summary>
        Task UpsertAsync(string symbol, string timeframeKey, DateOnly from, DateOnly to, CancellationToken ct = default);

        /// <summary>僅在下載流程明確確認左側無資料後，記錄最早有效資料日。</summary>
        Task ConfirmFirstTradableDateAsync(string symbol, string timeframeKey, DateOnly firstTradableDate, CancellationToken ct = default);

        /// <summary>依 requiredDays 計算相對 LCD 的缺口，若無缺口回傳 null。</summary>
        (DateOnly GapFrom, DateOnly GapTo)? ComputeMissingByDay(CoverageInfo cov, DateOnly lcd, int requiredDays);
    }
}

