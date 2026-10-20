# Trading System（MuNTrade）

**即時行情與交易執行系統｜C# · .NET · WPF**

原始個人專案整合券商 API、市場 Tick／Quote／K 線、交易委託、風控規則與桌面操作介面。隨著資料處理量及模組增加，資料傳遞、UI 更新與核心服務的責任分離成為主要工程挑戰，因此持續調整資料流與模組邊界。

這是系統演進的設計背景，**不是已經量測證實的效能提升宣稱**。

## 公開範圍：Paper-only Isolated Demo

> 本 Repository **不是原始 MuNTrade 完整系統**。公開 WPF 視窗是另建的精簡展示入口，非原始操作介面；本次不提供原系統截圖或影片。

原始系統需要券商環境、未公開的本地資料與設定。公開版擷取部分真正的事件傳遞、委託及規則程式，配合新建的安全組裝層，以**合成行情**驗證 Paper 委託流程。沒有券商 SDK、LIVE gateway、正式憑證或真實帳務，也不連線券商。

```text
Synthetic Tick / Quote
        ↓
Original InMemoryEventBus
        ↓
Market State + Virtual Clock
        ↓
Paper-only Admission + Original EntryWindowRule
        ↓
Adapted PaperTradingGateway
        ↓
Order / Fill Events → Offline Position State → Demo WPF
```

## 程式碼導覽

| 檔案 | 責任 |
|---|---|
| [`InMemoryEventBus.cs`](src/Source/MuNTrade.Core/Hubs/InMemoryEventBus.cs) | 來源事件訂閱與派送 |
| [`EntryWindowRule.cs`](src/Source/MuNTrade.Core/Trading/Rules/EntryWindowRule.cs) | 交易時間窗口規則 |
| [`PaperTradingGateway.cs`](src/Source/MuNTrade.Core/Trading/Gateways/PaperTradingGateway.cs) | 委託、限價成交、New／Filled 回報 |
| [`src/PaperSession.cs`](src/PaperSession.cs) | 本次新建的安全組裝、行情新鮮度、回報去重及持倉 |
| [`src/DemoAdapters.cs`](src/DemoAdapters.cs) | 合成時間及隔離行情 adapter |
| [`desktop/Program.cs`](desktop/Program.cs) | 新建的簡化 WPF Demo，不是原始 UI |
| [`tests/Program.cs`](tests/Program.cs) | 成交生命週期及失敗情境驗收 |

**架構與效能背景：** 原始專案曾因大量即時資料而需要重新思考傳輸通道及抽象層。但本公開切片無法單獨證明重構前後的效能差異，也未包含完整券商串接。此處僅描述實際面臨的工程問題及目前公開的模組責任。

## 本機執行與測試

需要 Windows 與 .NET SDK 10（含 ASP.NET Core shared framework）。

```powershell
dotnet build desktop/MuNTrade.Desktop.csproj
dotnet run --project tests/Acceptance.csproj
dotnet run --project desktop/MuNTrade.Desktop.csproj
```

展示流程：「合成行情」→「模擬委託」→「觸價成交」。也可測試 LIVE 拒絕、行情中斷及超時行情。

已驗證的公開切片涵蓋委託等待觸價、New／Filled 回報、重複與延遲成交回報、非法成交、LIVE 雙層拒絕、過量委託、過期／中斷行情、時間窗口，以及同秒委託 ID 衝突。完整測試範圍見 [docs/VALIDATION.md](docs/VALIDATION.md)。

## 已知限制（Known Limitations）

沒有真實券商 API 連線或下單、正式高流量效能測試、交易所佇列及延遲模擬、完整部分成交／改單流程或斷線重連驗收。新建的安全 gate 不代表原始完整 RiskManager 已被公開驗證。部分來源方法仍存在，但不在 Demo UI 操作範圍。編譯仍有一項未使用例外變數警告。

公開版包含 NuGet `packages.lock.json` 供還原版本；修改依賴後應重新測試。未公開任何原始策略、真實交易資料或私人資料庫。

## 來源與 AI 協作

我主導需求、系統方向、模組規劃討論及驗收；Codex 等 AI 參與架構細節與大量程式實作。本次公開版另由 AI 協助製作離線展示入口，不能視為原始完整系統。

來源 Commit：`0e70669d5b34d9fbd26bc63ce80574856301d9dd`。精選、調整及新建檔案分別記錄於 [FILE_ORIGINS.csv](FILE_ORIGINS.csv)、[SOURCE_MANIFEST.json](SOURCE_MANIFEST.json)。本程式碼公開供技術閱讀，未另行授予開源授權。
