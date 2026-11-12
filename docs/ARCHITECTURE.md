# MuNTrade 架構指南 (Architecture guide)

本文件依目前公開 repository 的實作說明主要元件與擴充方式。入口組裝範例是 headless CLI 回測；不包含未公開系統元件。

## 目錄 (Contents)

- [系統邊界](#系統邊界)
- [元件分層](#元件分層)
- [Tick 回放時序](#tick-回放時序)
- [DI 擴充註冊](#di-擴充註冊)
- [策略介面與生命週期](#策略介面與生命週期)
- [新增策略步驟](#新增策略步驟)
- [執行 CLI 回測](#執行-cli-回測)
- [替換 Tick 資料](#替換-tick-資料)
- [輸出與事件 journal](#輸出與事件-journal)
- [限制與驗證狀態](#限制與驗證狀態)

## 系統邊界

公開方案由 [MuNTrade.sln](../MuNTrade.sln) 中的 abstractions、core、infrastructure、CLI、資料產生器和 tests 組成。

CLI 使用 [DemoCli.BuildServices](../src/MuNTrade.Demo.Cli/DemoCli.cs) 建立 .NET DI container，再執行回測或報表命令。

[AppMode](../src/MuNTrade.Abstractions/Domain/Enums/SystemEnums/AppMode.cs) 描述服務組態的執行模式。

本指南聚焦 AppMode.Backtest 的服務生命週期。

AppMode.Execution 是 Core 與 Infrastructure 可組裝的服務模式，不表示此公開 repo 提供可用券商連線。

## 元件分層

| 層 | 主要責任 | 代表程式 |
| --- | --- | --- |
| 契約與領域 | 事件、介面、資料模型、設定契約 | [MuNTrade.Abstractions](../src/MuNTrade.Abstractions/) |
| 核心資料處理 | session filter、K 線生成、事件匯流排 | [SessionFilterService](../src/MuNTrade.Core/Services/SessionFilterService.cs)、[KLineBuilderService](../src/MuNTrade.Core/DataProcessing/KLine/KLineBuilderService.cs)、[InMemoryEventBus](../src/MuNTrade.Core/Hubs/InMemoryEventBus.cs) |
| 策略 runtime | registry、lifecycle、event routing、output routing | [StrategyRuntime](../src/MuNTrade.Core/StrategyRuntime/) |
| OrderBook | 委託簿事件、品質計算、流動性訊號 | [OrderBookEngine](../src/MuNTrade.Core/OrderBook/OrderBookEngine.cs)、[LiquidityFlowDetector](../src/MuNTrade.Core/OrderBook/LiquidityFlowDetector.cs) |
| 交易與帳務 | intent 執行、風控、成交、position 與 FIFO ledger | [Trading](../src/MuNTrade.Core/Trading/)、[FifoLedgerService](../src/MuNTrade.Core/Services/FifoLedgerService.cs) |
| 回測 | 虛擬時鐘、warmup、tick replay、模擬 gateway、summary | [Backtesting](../src/MuNTrade.Core/Backtesting/) |
| Infrastructure | tick 檔案來源、歷史 K 線來源、日曆 | [FileTickPlayer](../src/MuNTrade.Infrastructure/DataAccess/FileSystem/FileTickPlayer.cs)、[InfrastructureServicesRegistration](../src/MuNTrade.Infrastructure/Extensions/InfrastructureServicesRegistration.cs) |
| Demo 與報表 | CLI 參數、依賴組裝、回測、單頁 HTML | [MuNTrade.Demo.Cli](../src/MuNTrade.Demo.Cli/) |

主要依賴方向是 CLI 組合 Core 與 Infrastructure 實作；Core 以 Abstractions 中的介面接收來源、時鐘和委託閘道。

## Tick 回放時序

回測主流程由 [BacktestHost.RunAsync](../src/MuNTrade.Core/Backtesting/BacktestHost.cs) 控制，每個交易日、商品都在獨立的 scoped service scope 中執行。

1. [BacktestUniversePlanner](../src/MuNTrade.Core/Backtesting/Universe/BacktestUniversePlanner.cs) 依日期範圍和設定商品建立回測清單。
2. 回測時鐘先移至交易日開盤前的 warmup 時間。
3. [WarmupCacheManager](../src/MuNTrade.Core/Backtesting/Warmup/WarmupCacheManager.cs) 從本機 K 線 provider 載入策略所需歷史 bars。
4. StrategyHost 建立 strategy instance、設定市場輸入，再呼叫 strategy start。
5. [FileTickPlayer.StreamTicksAsync](../src/MuNTrade.Infrastructure/DataAccess/FileSystem/FileTickPlayer.cs) 讀取該商品當日 tick 檔。
6. 每筆 tick 到達時，SimClock.AdvanceTo 將回測時間更新為 tick timestamp。
7. SessionFilterService.ProcessTick 驗證該行情是否位於可接受時段。
8. 有效行情送入 KLineBuilderService.ProcessTick，更新中的 1 分 K bar 依設定產生事件。
9. 回測流程向 event bus 發布 TradeTickEvent，並按回放時間產生 StrategyPulse。
10. MarketEventRouter 依訂閱與策略 ingress 條件將事件分派到 StrategyHost 所管理的策略。
11. 同一筆回放 tick 也交給 BacktestOrderGateway.OnTick，供待處理模擬委託檢查成交條件。
12. 成交事件回到 event bus，供 position 管理與 journal 訂閱者更新狀態。
13. 收盤後回測會處理未完成 bar、策略停止與未平倉結束流程，再寫出該商品 journal 與 summary。

步驟 10 的背景分派與步驟 11 的 tick 撮合可能有非同步交錯；事件觀察次序不等於單一同步呼叫堆疊。

### 單筆行情的概念圖

~~~mermaid
sequenceDiagram
    participant Player as FileTickPlayer
    participant Clock as SimClock
    participant Filter as SessionFilterService
    participant Builder as KLineBuilderService
    participant Bus as InMemoryEventBus
    participant Router as MarketEventRouter
    participant Strategy as StrategyHost / IStrategy
    participant Gateway as BacktestOrderGateway
    participant Journal as BacktestEventJournal

    Player->>Clock: AdvanceTo(tick timestamp)
    Player->>Filter: ProcessTick(tick)
    Filter->>Builder: accepted tick
    Builder->>Bus: trade and bar events
    Player->>Bus: TradeTickEvent and StrategyPulse
    Bus-->>Router: async dispatch
    Router-->>Strategy: subscribed events
    Player->>Gateway: OnTick(tick)
    Gateway->>Bus: OrderFillEvent when matched
    Bus-->>Journal: subscribed event
~~~

[FileTickPlayer](../src/MuNTrade.Infrastructure/DataAccess/FileSystem/FileTickPlayer.cs) 讀取 UTC offset 不含於欄位的時間字串格式 yyyy-MM-dd HH:mm:ss.fff，數字以 invariant culture 解析。

此回測將 tick timestamp 傳入 SimClock；本地展示資料時段按 TWSE day session 設定。

## DI 擴充註冊

CLI 在 [DemoCli.BuildServices](../src/MuNTrade.Demo.Cli/DemoCli.cs) 呼叫四個主要 Core 註冊擴充，再加入 Infrastructure registrations。

### AddMuNTradeCoreBase

[AddMuNTradeCoreBase](../src/MuNTrade.Core/Extensions/CoreServicesRegistration.cs) 綁定主要選項、IEventBus 與共享 core services。

Event bus 的生命週期依 AppMode 設定；Execution 模式使用 singleton，非 Execution 模式以 scoped 註冊。

這是其它三個 pipeline/service registration 的共用基礎。

### AddTickPipeline

[AddTickPipeline](../src/MuNTrade.Core/Extensions/CoreServicesRegistration.cs) 註冊 RawTickChannel 作為 tick ingress buffer。

Execution 模式會加上 hosted SessionFilterService；batch/backtest 由 scoped SessionFilterService 和 FilteredTickChannel 管理。

### AddBarPipelines

[AddBarPipelines](../src/MuNTrade.Core/Extensions/CoreServicesRegistration.cs) 會再呼叫 KLine pipeline 註冊。

Execution 模式的 KLineHub 與 builder service 使用 singleton 和 hosted service。

Batch/backtest 模式會使用 scoped KLineBuilderService，讓每次回測 scope 持有自己的處理狀態。

### AddBacktestingServices

[AddBacktestingServices](../src/MuNTrade.Core/Extensions/CoreServicesRegistration.cs) 註冊 backtest 專屬依賴，包含 scoped SimClock、subscription manager、trade execution manager、intent adapter、position manager、risk manager、backtest risk gate、MarketEventRouter、universe planner、warmup provider/cache、report writer 與 BacktestHost。

該擴充也註冊明確列出的策略 templates 與 backtest gateway adapter。

### Infrastructure registrations

[AddMuNTradeInfrastructure](../src/MuNTrade.Infrastructure/Extensions/InfrastructureServicesRegistration.cs) 註冊 HistoricalDataReader、CsvHolidayCalendar、session provider、export/warmup services 及 ITickSource 的檔案實作 FileTickPlayer。

在 AppMode.Backtest 下，IOrderGateway 對應 BacktestOrderGateway。

在 AppMode.Execution 下則註冊 routing/paper gateway 實作；此 repo 沒有可連接公開 broker 的 adapter。

## 策略介面與生命週期

### IStrategy

[IStrategy](../src/MuNTrade.Abstractions/Contracts/Strategy/IStrategy.cs) 定義策略識別、版本、訂閱資訊、非同步啟動與停止，以及 OnEventAsync 事件 callback。

策略應從 callback 的事件型別和 payload 取得資料，並以 runtime 提供的輸出介面發出意圖或狀態。

### IStrategyTemplate

[IStrategyTemplate](../src/MuNTrade.Abstractions/Contracts/Strategy/IStrategyTemplate.cs) 提供 template ID、版本、warmup requirements 與 strategy instance factory。

CLI 設定的 StrategyTemplateId 由 StrategyRegistry 查找；template 建立回測所用 instance。

### Optional ingress specification

[IStrategyIngressSpec](../src/MuNTrade.Abstractions/Contracts/Strategy/IStrategyIngressSpec.cs) 是可選輸入描述，策略可指出所需商品、週期或指標 keys。

[MarketEventRouter](../src/MuNTrade.Core/StrategyRuntime/Routing/MarketEventRouter.cs) 將事件分派到符合訂閱的策略 instance。

### Runtime context

[IStrategyRuntimeContext](../src/MuNTrade.Abstractions/Contracts/Strategy/IStrategyRuntimeContext.cs) 向策略提供 event bus、IClock、logger、account key 與 output router 等執行環境服務。

策略實作可依賴這些抽象，避免自行建立全域時鐘或事件匯流排。

### Template registration

[StrategyTemplateRegistration.AddAllStrategyTemplates](../src/MuNTrade.Core/StrategyRuntime/Hosting/StrategyTemplateRegistration.cs) 以明確的 DI 註冊加入公開的驗證 template。

目前這些 template 是 code-based explicit registration，並非執行期掃描任意 assembly。

## 新增策略步驟

1. 在 Core 或獨立 strategy project 實作 IStrategy，保存 instance/template identity 並實作 start、stop、subscription 與 event callback。
2. 實作 IStrategyTemplate，提供唯一 template ID、版本、warmup requirements，並在 factory 依 instance context 建立策略。
3. 如需 router 依資料需求縮小輸入，讓策略 template 同時實作選用的 IStrategyIngressSpec，宣告 symbols、periods、indicator keys。
4. 將 template 加入 [StrategyTemplateRegistration](../src/MuNTrade.Core/StrategyRuntime/Hosting/StrategyTemplateRegistration.cs) 的明確 DI 註冊。
5. 若需策略參數，更新 template 對應設定／參數 schema，並確認 demo config 的 StrategyTemplateId 指向該 ID。
6. 以 dotnet run --project src/MuNTrade.Demo.Cli -c Release -- list-strategies 檢查註冊後 ID。
7. 用 CLI 對小日期區間執行回測，檢查 backtest_summary.csv、strategy event journal 與 report。

策略的訂閱、warmup 與參數 shape 必須符合現行 runtime/contracts；新增 template 不會自動讓現有測試覆蓋其行為。

## 執行 CLI 回測

先在 repo 根目錄還原並建置：

~~~bash
dotnet restore MuNTrade.sln
dotnet build MuNTrade.sln -c Release
~~~

列出已註冊策略 template：

~~~bash
dotnet run --project src/MuNTrade.Demo.Cli -c Release -- list-strategies
~~~

執行目前設定：

~~~bash
dotnet run --project src/MuNTrade.Demo.Cli -c Release -- backtest
~~~

指定單一商品、日期範圍、策略與輸出路徑：

~~~bash
dotnet run --project src/MuNTrade.Demo.Cli -c Release -- backtest --symbol 9901 --from 2024-03-11 --to 2024-03-11 --strategy Verification.Probe.MaCross --out out
~~~

CLI options 定義於 [CliArguments](../src/MuNTrade.Demo.Cli/CliArguments.cs)，預設日期、symbols、strategy、data roots 及 fill settings 在 [appsettings.demo.json](../src/MuNTrade.Demo.Cli/appsettings.demo.json)。

report 子命令將現有回測結果轉成靜態報表：

~~~bash
dotnet run --project src/MuNTrade.Demo.Cli -c Release -- report --in out --site site
~~~

## 替換 Tick 資料

### 檔案格式

[FileTickPlayer](../src/MuNTrade.Infrastructure/DataAccess/FileSystem/FileTickPlayer.cs) 依設定的 tick root 找商品子目錄及日期檔案。

預設檔案路徑格式為 <tickRoot>/<symbol>/<yyyyMMdd>_D_<symbol>.csv.gz。

gzip 解開後是 UTF-8 CSV，欄位標題如下：

~~~text
Timestamp,Symbol,Market,Price,Volume
~~~

Timestamp 必須符合 yyyy-MM-dd HH:mm:ss.fff；Price 與 Volume 以 invariant culture 解析。

檔案需按 tick timestamp 排列，且 symbol 欄位要與檔案所代表的商品一致。

### K 線與 warmup

Warmup provider 從設定的 KLineJsonBasePath 找每日 K 線 JSON。

每個 record 使用 KlineTime、Open、High、Low、Close、Qty 欄位。

策略在 StartAsync 前會依 template warmup requirements 載入可用歷史 bars；缺少必要資料時可調整 SkipMissingData 行為，但需留意失去 warmup 的策略條件。

假日由 HolidayCalendarPath 提供；週末由資料讀取及回測日曆邏輯排除。

### 設定自己的資料根目錄

1. 保留輸入檔名、CSV 欄名與時間／數字格式契約。
2. 把資料放在商品子目錄並依交易日期命名。
3. 在 src/MuNTrade.Demo.Cli/appsettings.demo.json 修改 DataSourceSettings.TickDataBasePath、KLineJsonBasePath 及 HolidayCalendarPath，或使用支援的設定來源覆寫。
4. 更新 BacktestSettings.StockIds 和日期區間。
5. 先執行單商品、單日回測檢查 missing data，再擴大範圍。

[SampleDataGenerator](../tools/MuNTrade.SampleData/Program.cs) 負責 repo 內可重建的範例輸入；它不會下載市場行情。

## 輸出與事件 journal

[BacktestReportWriter](../src/MuNTrade.Core/Backtesting/BacktestReportWriter.cs) 寫出回測 summary 與每商品/日期 detail。

CLI demo 的主要檔案是 out/backtest_summary.csv、out/backtest_metadata.json 和 out/strategy-events/ 下的 journal。

[BacktestEventJournal](../src/MuNTrade.Core/Backtesting/BacktestEventJournal.cs) 記錄 StrategyPacketEmittedEvent、OrderFillEvent、CompletedTrade、StrategyStateChangedEvent。

Journal 不包含 order events，因此不能單靠它還原所有委託生命週期或交易所佇列狀態。

[StaticReportGenerator](../src/MuNTrade.Demo.Cli/StaticReportGenerator.cs) 讀取回測輸出並寫出自包含的 site/index.html。

報表只需瀏覽器開啟，不載入外部 CDN 或線上圖表服務。

FIFO 實現損益服務 [FifoLedgerService](../src/MuNTrade.Core/Services/FifoLedgerService.cs) 在 execution service registration 中使用；headless batch backtest 的 BacktestHost 使用自己的回測 ledger/results 計算。

## 限制與驗證狀態

- MarketEventRouter 以非同步方式分派 strategy events，事件與診斷觀察順序可能變動。
- 按商品序列化的 event queue 加明確 drain 邊界，是改善 router 可診斷性的候選方向。
- BacktestOrderGateway 的 fill model 不模擬 exchange queue、網路延遲或部分成交。
- 回測 journal 不包含 order events，無法取代完整委託稽核紀錄。
- BacktestReportWriter 使用平台文字寫入行為，backtest_summary.csv 跨 OS 換行可能不同。
- CI 會在同一個 job 內比較兩次 summary hash；跨 OS summary bytes 並沒有作為目前比較項目。
- Linux 上的建置、測試與 demo 由 CI 的 Ubuntu job 驗證。
- Dockerfile 尚未納入 CI。
- 可在 [CI workflow](../.github/workflows/ci.yml)、[generator verification script](../scripts/verify-samples.sh) 和 [Demo tests](../tests/MuNTrade.Demo.Tests/DemoWorkflowTests.cs) 查閱自動化驗證入口。