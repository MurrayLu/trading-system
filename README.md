# Trading System（MuNTrade）

**即時行情與交易執行系統（Market Data & Trading System）｜安全的離線模擬委託流程**

> 公開程式為隔離展示（Isolated Demo）。桌面視窗是本次新建的精簡 WPF 入口，不是原始 MuNTrade 操作介面。本次不公開任何截圖或影片。

將合成行情送進事件匯流排，經風控規則與真正的 Paper gateway，呈現委託接受、觸價成交及持倉更新。展示程式沒有券商 SDK、憑證或 LIVE gateway；不連線券商、不宣稱實盤績效。


## 需求背景與架構（Architecture）

即時交易程式要處理行情、時間、訂單與回報的一致性，並明確隔離 LIVE 與 PAPER。本候選從實際 `TradeApp` 的 `MunTrade` 分支取材；保留原始 EventBus、委託契約、撮合邏輯與進場時間窗口規則，新增安全展示入口。

```text
Mock Broker / synthetic Tick + quote
              ↓ original InMemoryEventBus
Market State + deterministic clock
              ↓ new paper-only admission + original EntryWindowRule
adapted original PaperTradingGateway
              ↓ original BrokerReportEvent / OrderFillEvent
new idempotent paper state → new offline WPF monitoring host
```

## 本機操作（Local Demo）

需要 Windows 與 .NET SDK 10（含 ASP.NET Core shared framework；未使用網路服務）。

```powershell
dotnet build desktop/MuNTrade.Desktop.csproj
dotnet run --project tests/Acceptance.csproj
dotnet run --project desktop/MuNTrade.Desktop.csproj
```

依序點「1. 合成行情」→「2. 模擬委託」→「3. 觸價成交」，再點「驗證 LIVE 拒絕」。可另外點「行情中斷」後再次委託，查看拒絕原因。每次啟動都是獨立的合成工作階段，沒有真實帳務與持倉讀取。

## 程式閱讀導覽（Code Walkthrough）

| 路徑 | 來源與責任 |
|---|---|
| `src/Source/MuNTrade.Core/Hubs/InMemoryEventBus.cs` | 實際來源事件派送，保留訂閱與退訂機制 |
| `src/Source/MuNTrade.Core/Trading/Rules/EntryWindowRule.cs` | 實際時間窗口規則；建構式改為明示合成時間設定 |
| `src/Source/MuNTrade.Core/Trading/Gateways/PaperTradingGateway.cs` | 真正的委託簿、限價觸價、New／Filled 回報；候選額外拒絕 LIVE 與非法數量 |
| `src/Source/MuNTrade.Abstractions/`、`src/TimeAxes.cs` | 實際訂單、Tick、quote、回報、時間及 gateway 契約 |
| `src/PaperSession.cs` | 新建安全組合根（Composition Root）、過期行情／斷線／數量 gate、去重回報與持倉 |
| `src/DemoAdapters.cs` | 新建離線 snapshot 邊界與虛擬時鐘；未聲稱原始全套 MarketSnapshotBuilder 已搬入 |
| `desktop/Program.cs` | 新建精簡 WPF 監看入口；不是原始完整交易桌面 |
| `tests/Program.cs` | 真實 paper lifecycle、LIVE 雙層拒絕、回報與風控失敗情境 |

## 主要決策與驗收

從候選組件移除 LIVE gateway 標記與券商參照，並在 gateway 自身拒絕 IsLive，避免繞過展示入口。測試會直接嘗試 gateway 的 LIVE 請求，檢查沒有成交，並檢查組件沒有券商參照。原始秒內循環委託序號可能碰撞，候選改為遞增 Paper ID，保留同秒多筆委託回歸驗收。合成狀態以 OrderId 去重，保留合法延遲回報，拒絕非法回報。

驗收涵蓋正常排隊與觸價、重複／延遲／非法回報、LIVE 拒絕、過量委託、過期行情、行情中斷、時間窗口拒絕與序號碰撞。完整命令與範圍見 [VALIDATION](docs/VALIDATION.md)。

## 本人角色與 AI 協作

本作品集由本人選定公開範圍並核准發布。本輪使用 AI 協助讀碼、擷取、建立隔離展示入口、合成資料、測試與文件；來源原碼、調整碼及新建展示碼分別記錄在來源清單。原始專案的精確分工與歷史 AI 使用情形未在本次查核，因此不主張全部獨立手寫、手寫比例、正式部署或完整原系統驗收。

## 已知限制（Known Limitations）

這是有完整輸入／風控／委託／回報／狀態／UI 的安全切片；新增安全 gate 不代表來源完整 RiskManager 的所有風控均已驗收。不公開任何原始策略、券商帳務或 SDK。部分原始 gateway 的改價／減量等方法仍在來源碼，但未由本展示 UI 開放，亦未列入完成驗收。無真實撮合延遲、部分成交、交易所佇列、跨執行緒高流量或正式重連驗收。原始 EventBus 會隔離訂閱者例外，此行為保留且未宣稱具有完整錯誤觀測；編譯保留一個未使用例外變數警告。


[來源與修改對照](SOURCE_MANIFEST.json) · [檔案分類](FILE_ORIGINS.csv)

依賴重現性：本候選附 NuGet `packages.lock.json`，預設 locked restore；若修改套件版本，須另行重驗，不可沿用本次證據。

來源 Commit SHA：`0e70669d5b34d9fbd26bc63ce80574856301d9dd`。保留內部 namespace 與型別名稱以利來源對照；未包含來源未提交修改。
