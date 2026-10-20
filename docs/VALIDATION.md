# 發布驗證（Validation）

驗證日期：2026-10-20；Windows、Node.js 24.14.0 / .NET SDK 10。下列命令全部 exit 0。

```powershell
dotnet build desktop/MuNTrade.Desktop.csproj --nologo
dotnet run --project tests/Acceptance.csproj
dotnet list src/MuNTrade.Demo.Core.csproj package --vulnerable --include-transitive
```

5 組情境通過：Paper lifecycle 與重複／延遲／非法回報、LIVE 雙層拒絕且無券商 assembly、數量／過期行情／斷線 gate、原始時間窗口、同秒委託 ID 不碰撞。WPF 編譯成功，0 errors；保留來源 EventBus 的 CS0168 警告。套件掃描未回報已知漏洞。未重新執行桌面互動驗收，沒有原始 WPF 畫面證據；不代表券商或實盤驗收。

公開桌面為新建隔離展示（Isolated Demo），不是原始系統畫面。本次不發布截圖、影片或原始正式環境資料。
