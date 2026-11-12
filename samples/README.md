# 合成回測樣本資料

此目錄包含由 `tools/MuNTrade.SampleData` 產生的合成資料，只用於展示與驗證回測流程，不取自或改寫任何交易所行情。

## 內容與格式

- `ticks/<商品>/<yyyyMMdd>_D_<商品>.csv.gz`：日盤 tick CSV，UTF-8，欄位為 `Timestamp,Symbol,Market,Price,Volume`。時間格式為 `yyyy-MM-dd HH:mm:ss.fff`，讀取器使用第 1、4、5 欄作為時間、價格與成交量。
- `klines/<商品>/1 分 K 線_<開始日期>_<結束日期>.json`：1 分 K 歷史 JSON，欄位為 `KlineTime`、`Open`、`High`、`Low`、`Close`、`Qty`。2024-03-08 提供每檔 120 根 warmup K 線；五個回測日期各有每日 K 線。
- `holidays.txt`：本樣本使用的假日清單；空清單表示只排除週末。

商品代號 `9901`–`9905` 為本專案虛構識別碼。資料涵蓋 2024-03-11 至 2024-03-15 五個平日。每檔每日 4,080 筆 tick；開盤後與收盤前每分鐘 19 筆，盤中每分鐘 14 筆，時間戳精確到毫秒且間隔不規則。成交量以一般小單為主，偶爾出現較大成交量。

價格由固定 seed `0x5A17C0DE` 的帶雜訊隨機過程產生，再為不同商品疊加盤整、趨勢、急拉回落、假突破及下跌後反彈型態。樣本不以策略獲利為目標，因此不同商品或日期出現虧損是正常的。商品價格介於 30 至 38 元，符合台股 0.05 元升降單位；每日 K 線的 OHLC 與成交量由同日 ticks 聚合而來。整個 `samples/` 目錄應小於 20 MB。

## 重新產生

在 repo 根目錄執行：

```powershell
dotnet run --project tools/MuNTrade.SampleData -c Release
```

產生器使用固定 seed，重複執行會產生相同內容與檔案雜湊。可用 `--out <dir>` 指定輸出目錄。

## 使用自己的資料

將 tick 與 warmup K 線整理成上述目錄及 schema，並用 `MUNTRADE_` 環境變數或修改 `src/MuNTrade.Demo.Cli/appsettings.demo.json` 的 `DataSourceSettings` 路徑指向資料根目錄。tick 檔名、時間欄位、K 線 JSON 欄位及交易日曆格式需符合讀取器預期；回測 CLI 不會下載行情或連線券商。
