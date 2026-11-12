using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class StaticReportGenerator
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] SeriesColors = ["#2f6bde", "#d45a4c", "#16866f", "#9a55b4", "#d18a23", "#60778e", "#c14f8a"];

    public static string Generate(string inputRoot, string siteRoot)
    {
        var snapshot = ReadSnapshot(inputRoot);
        Directory.CreateDirectory(siteRoot);
        var pagePath = Path.Combine(siteRoot, "index.html");
        var html = Render(snapshot).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        File.WriteAllText(pagePath, html, new UTF8Encoding(false));
        return pagePath;
    }

    private static ReportSnapshot ReadSnapshot(string inputRoot)
    {
        if (!Directory.Exists(inputRoot)) throw new ArgumentException($"回測輸出目錄不存在：{inputRoot}");
        var markerPath = Path.Combine(inputRoot, DemoOutputDirectory.MarkerName);
        var manifestPath = Path.Combine(inputRoot, DemoOutputDirectory.ManifestName);
        var summaryPath = Path.Combine(inputRoot, "backtest_summary.csv");
        var metadataPath = Path.Combine(inputRoot, "backtest_metadata.json");
        if (!File.Exists(markerPath) || !string.Equals(File.ReadAllText(markerPath), DemoOutputDirectory.MarkerContent, StringComparison.Ordinal) ||
            !File.Exists(manifestPath) || !File.Exists(summaryPath) || !File.Exists(metadataPath))
            throw new ArgumentException($"不是有效的 MuNTrade.Demo.Cli 回測輸出目錄（缺少標記、清單、摘要或 metadata）：{inputRoot}");

        string[] manifest;
        try { manifest = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? []; }
        catch (JsonException) { throw new ArgumentException($"回測輸出目錄清單無法辨識：{manifestPath}"); }
        if (!manifest.Contains("F:backtest_summary.csv", StringComparer.OrdinalIgnoreCase) ||
            !manifest.Contains("F:backtest_metadata.json", StringComparer.OrdinalIgnoreCase) ||
            !manifest.Any(x => x.StartsWith("F:details/", StringComparison.OrdinalIgnoreCase) && x.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"不是有效的 MuNTrade.Demo.Cli 回測輸出目錄（清單缺少必要檔案）：{inputRoot}");

        BacktestMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<BacktestMetadata>(File.ReadAllText(metadataPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("metadata is empty");
        }
        catch (JsonException ex) { throw new ArgumentException($"回測 metadata 無法辨識：{ex.Message}"); }

        var summaryDates = ReadSummaryDates(summaryPath);
        var symbols = metadata.Symbols.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var trades = new List<TradeRecord>();
        var fillsBySymbol = symbols.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var journalFiles = Directory.EnumerateFiles(Path.Combine(inputRoot, "details"), "*.jsonl", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (journalFiles.Length == 0) throw new ArgumentException($"回測輸出目錄找不到 details/*.jsonl：{inputRoot}");

        foreach (var journalPath in journalFiles)
        {
            var fileSymbol = Path.GetFileNameWithoutExtension(journalPath);
            if (!fillsBySymbol.ContainsKey(fileSymbol)) fillsBySymbol[fileSymbol] = 0;
            foreach (var line in File.ReadLines(journalPath))
            {
                using var document = JsonDocument.Parse(line);
                var envelope = document.RootElement;
                var type = ReadString(GetProperty(envelope, "Type"));
                var evt = GetProperty(envelope, "Event");
                if (type == "OrderFillEvent")
                {
                    fillsBySymbol[fileSymbol]++;
                    continue;
                }
                if (type == "CompletedTrade")
                {
                    var symbol = ReadString(GetProperty(evt, "StockId")) ?? fileSymbol;
                    if (!fillsBySymbol.ContainsKey(symbol)) fillsBySymbol[symbol] = 0;
                    var entryTime = GetDateTime(evt, "EntryTime");
                    var exitTime = GetDateTime(evt, "ExitTime");
                    trades.Add(new TradeRecord(
                        symbol,
                        entryTime,
                        exitTime,
                        ReadDirection(GetProperty(evt, "Direction")),
                        GetDecimal(evt, "EntryPrice"),
                        GetDecimal(evt, "ExitPrice"),
                        GetInt64(evt, "Quantity"),
                        GetDecimal(evt, "GrossPnL"),
                        GetDecimal(evt, "Commission") + GetDecimal(evt, "Tax"),
                        GetDecimal(evt, "NetPnL"),
                        journalPath));
                }
            }
        }

        symbols = symbols.Union(fillsBySymbol.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sortedTrades = trades.OrderBy(x => x.ExitTime).ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.EntryTime).ThenBy(x => x.Source, StringComparer.Ordinal).ToArray();
        var start = summaryDates.Count == 0 ? metadata.StartDate : summaryDates.Min();
        var end = summaryDates.Count == 0 ? metadata.EndDate : summaryDates.Max();
        return new ReportSnapshot(
            symbols,
            summaryDates.OrderBy(x => x).ToArray(),
            fillsBySymbol,
            sortedTrades,
            metadata.TotalTicks,
            metadata.StrategyTemplateId,
            metadata.TickSource,
            start,
            end);
    }

    private static HashSet<DateOnly> ReadSummaryDates(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine();
        if (header is null) throw new ArgumentException($"回測摘要 CSV 是空檔：{path}");
        var columns = ParseCsvLine(header);
        var dateIndex = columns.FindIndex(x => string.Equals(x, "TradeDate", StringComparison.OrdinalIgnoreCase));
        var skippedIndex = columns.FindIndex(x => string.Equals(x, "Skipped", StringComparison.OrdinalIgnoreCase));
        if (dateIndex < 0) throw new ArgumentException($"回測摘要 CSV 缺少 TradeDate 欄位：{path}");
        var dates = new HashSet<DateOnly>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var fields = ParseCsvLine(line);
            if (fields.Count <= dateIndex || (skippedIndex >= 0 && fields.Count > skippedIndex && fields[skippedIndex] == "1")) continue;
            if (DateOnly.TryParseExact(fields[dateIndex], "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date)) dates.Add(date);
        }
        if (dates.Count == 0) throw new ArgumentException($"回測摘要 CSV 沒有有效交易日期：{path}");
        return dates;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }
        fields.Add(field.ToString());
        return fields;
    }

    private static string Render(ReportSnapshot data)
    {
        var completedCount = data.Trades.Length;
        var gross = data.Trades.Sum(x => x.GrossPnL);
        var costs = data.Trades.Sum(x => x.Costs);
        var net = data.Trades.Sum(x => x.NetPnL);
        var winRate = completedCount == 0 ? 0m : data.Trades.Count(x => x.NetPnL > 0m) * 100m / completedCount;
        var fills = data.FillsBySymbol.Values.Sum();
        var html = new StringBuilder(Math.Min(900_000, 9_000 + completedCount * 420));
        html.Append("<!doctype html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>MuNTrade · 靜態回測報表</title><style>").Append(Styles).Append("</style></head><body><main class=\"page\">");
        html.Append("<header class=\"hero\"><div class=\"eyebrow\">MUNTRADE · SYNTHETIC BACKTEST</div><h1>MuNTrade 回測展示</h1><p>一份可離線檢視的範例回測摘要與成交紀錄。</p>");
        html.Append("<div class=\"notice\"><strong>重要聲明 Disclaimer</strong><br>資料為合成資料；策略為驗證用範例（均線交叉 probe）。下列損益數字不代表任何真實績效，也不構成投資建議。</div></header>");
        html.Append("<section class=\"panel\"><div class=\"section-heading\"><div><div class=\"eyebrow\">01 · SUMMARY</div><h2>總覽 Overview</h2></div></div><div class=\"metrics\">");
        Metric(html, "商品數 Symbols", data.Symbols.Length.ToString(Invariant));
        Metric(html, "交易日數 Trading days", data.Dates.Length.ToString(Invariant));
        Metric(html, "Tick 總數 Ticks", data.TickCount.ToString("N0", Invariant));
        Metric(html, "成交筆數 Fills", fills.ToString("N0", Invariant));
        Metric(html, "完成交易 Round trips", completedCount.ToString("N0", Invariant));
        Metric(html, "毛損益 Gross P&L", FormatMoney(gross), PnlClass(gross));
        Metric(html, "交易成本 Costs", FormatMoney(costs));
        Metric(html, "淨損益 Net P&L", FormatMoney(net), PnlClass(net));
        Metric(html, "勝率 Win rate", winRate.ToString("0.0", Invariant) + "%");
        html.Append("</div></section>");

        html.Append("<section class=\"panel\"><div class=\"section-heading\"><div><div class=\"eyebrow\">02 · EQUITY CURVE</div><h2>累計淨損益曲線 Cumulative Net P&amp;L</h2></div><label class=\"select-label\">曲線範圍 <select id=\"chartSymbol\"><option value=\"all\">全部商品 All symbols</option>");
        foreach (var symbol in data.Symbols) html.Append("<option value=\"").Append(Attr(symbol)).Append("\">").Append(E(symbol)).Append("</option>");
        html.Append("</select></label></div><div class=\"chart-wrap\"><svg class=\"chart\" viewBox=\"0 0 920 280\" role=\"img\" aria-label=\"Cumulative realized net profit and loss\">");
        AppendChartGrid(html, data);
        html.Append("<g id=\"chartSeries\">");
        AppendChartSeries(html, "all", data.Trades, "#2f6bde", data);
        for (var i = 0; i < data.Symbols.Length; i++)
            AppendChartSeries(html, data.Symbols[i], data.Trades.Where(x => x.Symbol == data.Symbols[i]).ToArray(), SeriesColors[i % SeriesColors.Length], data);
        html.Append("</g></svg></div><p class=\"caption\">依完成交易出場時間排列；曲線呈現已實現淨損益。按商品切換可查看單一序列。</p></section>");

        html.Append("<section class=\"panel\"><div class=\"section-heading\"><div><div class=\"eyebrow\">03 · DAILY MATRIX</div><h2>商品 × 交易日淨損益 Product × Day Net P&amp;L</h2></div></div><div class=\"table-scroll\"><table><thead><tr><th>商品 Symbol</th>");
        foreach (var date in data.Dates) html.Append("<th class=\"number\">").Append(date.ToString("yyyy-MM-dd", Invariant)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var symbol in data.Symbols)
        {
            html.Append("<tr><th scope=\"row\">").Append(E(symbol)).Append("</th>");
            foreach (var date in data.Dates)
            {
                var value = data.Trades.Where(x => x.Symbol == symbol && DateOnly.FromDateTime(x.ExitTime) == date).Sum(x => x.NetPnL);
                var heat = HeatLevel(value, data.Trades);
                var sign = value > 0m ? "+" : value < 0m ? "−" : "";
                html.Append("<td class=\"number heat ").Append(value > 0m ? "positive" : value < 0m ? "negative" : "zero")
                    .Append(" heat-").Append(heat.ToString(Invariant)).Append("\">").Append(sign).Append(FormatMoney(value)).Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div><p class=\"caption\">正負數字同時使用正負號與色彩表示；空白交易日顯示 0。</p></section>");

        html.Append("<section class=\"panel\"><div class=\"section-heading\"><div><div class=\"eyebrow\">04 · BY SYMBOL</div><h2>商品摘要 Product Summary</h2></div></div><div class=\"table-scroll\"><table><thead><tr><th>商品 Symbol</th><th class=\"number\">成交 Fills</th><th class=\"number\">完成交易 Round trips</th><th class=\"number\">勝率 Win rate</th><th class=\"number\">毛損益 Gross P&amp;L</th><th class=\"number\">淨損益 Net P&amp;L</th></tr></thead><tbody>");
        foreach (var symbol in data.Symbols)
        {
            var trades = data.Trades.Where(x => x.Symbol == symbol).ToArray();
            var symbolGross = trades.Sum(x => x.GrossPnL);
            var symbolNet = trades.Sum(x => x.NetPnL);
            var symbolWin = trades.Length == 0 ? 0m : trades.Count(x => x.NetPnL > 0m) * 100m / trades.Length;
            html.Append("<tr><th scope=\"row\">").Append(E(symbol)).Append("</th><td class=\"number\">").Append(data.FillsBySymbol.GetValueOrDefault(symbol).ToString("N0", Invariant))
                .Append("</td><td class=\"number\">").Append(trades.Length.ToString("N0", Invariant)).Append("</td><td class=\"number\">").Append(symbolWin.ToString("0.0", Invariant)).Append("%</td><td class=\"number ")
                .Append(PnlClass(symbolGross)).Append("\">").Append(FormatMoney(symbolGross)).Append("</td><td class=\"number ").Append(PnlClass(symbolNet)).Append("\">").Append(FormatMoney(symbolNet)).Append("</td></tr>");
        }
        html.Append("</tbody></table></div></section>");

        html.Append("<section class=\"panel\"><div class=\"section-heading\"><div><div class=\"eyebrow\">05 · TRADE LOG</div><h2>完成交易明細 Completed Trades</h2></div><div class=\"filters\"><label>商品 Symbol <select id=\"filterSymbol\"><option value=\"\">全部 All</option>");
        foreach (var symbol in data.Symbols) html.Append("<option value=\"").Append(Attr(symbol)).Append("\">").Append(E(symbol)).Append("</option>");
        html.Append("</select></label><label>出場日期 Date <select id=\"filterDate\"><option value=\"\">全部 All</option>");
        foreach (var date in data.Dates) html.Append("<option value=\"").Append(date.ToString("yyyy-MM-dd", Invariant)).Append("\">").Append(date.ToString("yyyy-MM-dd", Invariant)).Append("</option>");
        html.Append("</select></label></div></div><div class=\"table-scroll\"><table id=\"tradesTable\"><thead><tr>");
        Header(html, "商品 Symbol", "symbol"); Header(html, "進場時間 Entry time", "entry"); Header(html, "出場時間 Exit time", "exit"); Header(html, "方向 Direction", "direction");
        Header(html, "進場價 Entry price", "entry-price", true); Header(html, "出場價 Exit price", "exit-price", true); Header(html, "數量 Quantity", "quantity", true);
        Header(html, "毛損益 Gross P&amp;L", "gross", true); Header(html, "成本 Costs", "costs", true); Header(html, "淨損益 Net P&amp;L", "net", true);
        html.Append("</tr></thead><tbody>");
        foreach (var trade in data.Trades)
        {
            var exitDate = DateOnly.FromDateTime(trade.ExitTime).ToString("yyyy-MM-dd", Invariant);
            html.Append("<tr data-symbol=\"").Append(Attr(trade.Symbol)).Append("\" data-date=\"").Append(exitDate).Append("\">");
            Cell(html, trade.Symbol, trade.Symbol); Cell(html, FormatTime(trade.EntryTime), trade.EntryTime.Ticks.ToString(Invariant)); Cell(html, FormatTime(trade.ExitTime), trade.ExitTime.Ticks.ToString(Invariant));
            Cell(html, trade.Direction, trade.Direction);
            NumberCell(html, trade.EntryPrice); NumberCell(html, trade.ExitPrice); NumberCell(html, trade.Quantity);
            NumberCell(html, trade.GrossPnL, PnlClass(trade.GrossPnL)); NumberCell(html, trade.Costs); NumberCell(html, trade.NetPnL, PnlClass(trade.NetPnL));
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div><p class=\"caption\">欄位來自 CompletedTrade 事件；點擊欄名排序，可依商品與出場日期篩選。</p></section>");

        html.Append("<footer class=\"footer\"><h2>資料來源 Data Sources</h2><p>回測日期範圍：").Append(data.StartDate.ToString("yyyy-MM-dd", Invariant)).Append(" 至 ").Append(data.EndDate.ToString("yyyy-MM-dd", Invariant))
            .Append(" · 策略範本 ID：<code>").Append(E(data.StrategyTemplateId)).Append("</code></p><p>Tick 數來自回測時讀取的合成 tick CSV.GZ 檔案（").Append(E(data.TickSource)).Append("，計數記錄於 backtest_metadata.json）；成交與完成交易來自 details/YYYYMMDD/&lt;symbol&gt;.jsonl；交易日來自 backtest_summary.csv。</p>")
            .Append("<p>此頁不含產生時間，便於重現與靜態部署。</p></footer></main><script>").Append(Script).Append("</script></body></html>");
        return html.ToString();
    }

    private static void AppendChartGrid(StringBuilder html, ReportSnapshot data)
    {
        var values = CumulativeValues(data.Trades);
        var min = Math.Min(0m, values.Count == 0 ? 0m : values.Min());
        var max = Math.Max(0m, values.Count == 0 ? 0m : values.Max());
        if (min == max) { min = -1m; max = 1m; }
        html.Append("<g class=\"grid\">");
        for (var index = 0; index < 5; index++)
        {
            var y = 18 + (index * 56);
            var amount = max - ((max - min) * index / 4m);
            html.Append("<line x1=\"64\" y1=\"").Append(y.ToString(Invariant)).Append("\" x2=\"908\" y2=\"").Append(y.ToString(Invariant)).Append("\"></line><text x=\"56\" y=\"").Append((y + 4).ToString(Invariant)).Append("\" text-anchor=\"end\">").Append(E(FormatMoney(amount))).Append("</text>");
        }
        html.Append("</g>");
    }

    private static void AppendChartSeries(StringBuilder html, string key, IReadOnlyList<TradeRecord> trades, string color, ReportSnapshot data)
    {
        var values = CumulativeValues(trades);
        var all = CumulativeValues(data.Trades);
        var min = Math.Min(0m, all.Count == 0 ? 0m : all.Min());
        var max = Math.Max(0m, all.Count == 0 ? 0m : all.Max());
        if (min == max) { min = -1m; max = 1m; }
        var points = new List<(decimal Index, decimal Value)> { (0m, 0m) };
        for (var i = 0; i < values.Count; i++) points.Add((i + 1m, values[i]));
        var denominator = Math.Max(1, values.Count);
        var coordinateText = string.Join(" ", points.Select(point =>
        {
            var x = 64m + point.Index / denominator * 844m;
            var y = 18m + (max - point.Value) / (max - min) * 224m;
            return $"{x.ToString("0.##", Invariant)},{y.ToString("0.##", Invariant)}";
        }));
        html.Append("<g data-chart-series=\"").Append(Attr(key)).Append("\" style=\"").Append(key == "all" ? "" : "display:none;").Append("\"><polyline fill=\"none\" stroke=\"")
            .Append(color).Append("\" stroke-width=\"3\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"").Append(coordinateText).Append("\"></polyline></g>");
    }

    private static List<decimal> CumulativeValues(IEnumerable<TradeRecord> trades)
    {
        decimal running = 0m;
        var values = new List<decimal>();
        foreach (var trade in trades) { running += trade.NetPnL; values.Add(running); }
        return values;
    }

    private static void Metric(StringBuilder html, string label, string value, string valueClass = "")
        => html.Append("<div class=\"metric\"><div class=\"metric-label\">").Append(E(label)).Append("</div><div class=\"metric-value number ").Append(valueClass).Append("\">").Append(E(value)).Append("</div></div>");

    private static void Header(StringBuilder html, string label, string sort, bool number = false)
        => html.Append("<th class=\"").Append(number ? "number " : "").Append("sortable\" data-sort=\"").Append(sort).Append("\" tabindex=\"0\">").Append(label).Append(" ↕</th>");

    private static void Cell(StringBuilder html, string label, string sortValue)
        => html.Append("<td data-sort-value=\"").Append(Attr(sortValue)).Append("\">").Append(E(label)).Append("</td>");

    private static void NumberCell(StringBuilder html, decimal value, string valueClass = "")
    {
        var number = value.ToString("0.################", Invariant);
        html.Append("<td class=\"number ").Append(valueClass).Append("\" data-sort-value=\"").Append(number).Append("\">").Append(E(value.ToString("N2", Invariant))).Append("</td>");
    }

    private static void NumberCell(StringBuilder html, long value, string valueClass = "")
        => html.Append("<td class=\"number ").Append(valueClass).Append("\" data-sort-value=\"").Append(value.ToString(Invariant)).Append("\">").Append(value.ToString("N0", Invariant)).Append("</td>");

    private static int HeatLevel(decimal value, IReadOnlyList<TradeRecord> trades)
    {
        if (value == 0m) return 0;
        var max = trades.Select(x => Math.Abs(x.NetPnL)).DefaultIfEmpty(0m).Max();
        return max == 0m ? 1 : Math.Clamp((int)Math.Ceiling(Math.Abs(value) / max * 5m), 1, 5);
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string? ReadString(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    private static decimal GetDecimal(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : 0m;
    }
    private static long GetInt64(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0L;
    }
    private static DateTime GetDateTime(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), Invariant, DateTimeStyles.RoundtripKind, out var date)) return date;
        return DateTime.MinValue;
    }
    private static string ReadDirection(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number == 0 ? "Buy 買進" : number == 1 ? "Sell 賣出" : number.ToString(Invariant)
            : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string FormatMoney(decimal value) => value.ToString("N2", Invariant);
    private static string PnlClass(decimal value) => value > 0m ? "gain" : value < 0m ? "loss" : "neutral";
    private static string FormatTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", Invariant);
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Attr(string value) => WebUtility.HtmlEncode(value).Replace("'", "&#39;", StringComparison.Ordinal);

    private const string Styles = """
        :root{color-scheme:light dark;--bg:#f2f5f8;--panel:#fff;--ink:#17212b;--muted:#617181;--line:#dce3ea;--accent:#265fc4;--notice:#fff4d5;--notice-ink:#503b08;--gain:#187455;--loss:#b53b35;--shadow:0 12px 32px rgba(21,39,58,.06)}
        @media(prefers-color-scheme:dark){:root{--bg:#111820;--panel:#19232d;--ink:#e6edf3;--muted:#a7b5c2;--line:#34414e;--accent:#86adff;--notice:#43381b;--notice-ink:#ffe7a1;--gain:#75d5ac;--loss:#ff928a;--shadow:0 12px 32px rgba(0,0,0,.18)}}
        *{box-sizing:border-box}html{overflow-x:hidden}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 system-ui,-apple-system,"Segoe UI",sans-serif;overflow-x:hidden}.page{width:min(1160px,100% - 32px);margin:32px auto 56px}.hero{padding:18px 4px 24px}.eyebrow{font-size:.72rem;font-weight:750;letter-spacing:.14em;color:var(--accent)}h1{font-size:clamp(2rem,4vw,3.1rem);letter-spacing:-.04em;margin:.3rem 0}h2{font-size:1.22rem;margin:.2rem 0}.hero>p,.caption{color:var(--muted)}.notice{margin-top:22px;padding:16px 18px;background:var(--notice);color:var(--notice-ink);border-left:4px solid #d29b23;border-radius:10px}.panel{margin:16px 0;padding:22px;background:var(--panel);border:1px solid var(--line);border-radius:16px;box-shadow:var(--shadow);min-width:0}.section-heading{display:flex;justify-content:space-between;align-items:flex-end;gap:16px;margin-bottom:18px}.metrics{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px}.metric{padding:14px 15px;border:1px solid var(--line);border-radius:12px;min-width:0}.metric-label{color:var(--muted);font-size:.83rem}.metric-value{font-size:clamp(1.05rem,2vw,1.45rem);font-weight:680;margin-top:6px}.number{font-variant-numeric:tabular-nums lining-nums;font-feature-settings:"tnum" 1;text-align:right;white-space:nowrap}.gain,.positive{color:var(--gain)}.loss,.negative{color:var(--loss)}.neutral,.zero{color:var(--muted)}.select-label,.filters label{display:flex;align-items:center;gap:8px;color:var(--muted);font-size:.86rem}select{max-width:100%;padding:8px 10px;border:1px solid var(--line);border-radius:8px;background:var(--panel);color:var(--ink);font:inherit}.chart-wrap{width:100%;min-width:0}.chart{display:block;width:100%;height:auto;min-height:210px}.grid line{stroke:var(--line);stroke-width:1}.grid text{fill:var(--muted);font-size:11px;font-variant-numeric:tabular-nums}.caption{font-size:.82rem;margin:.8rem 0 0}.table-scroll{width:100%;overflow-x:auto;overscroll-behavior-x:contain;border:1px solid var(--line);border-radius:10px}table{border-collapse:collapse;width:100%;font-size:.88rem}th,td{padding:10px 12px;border-bottom:1px solid var(--line);text-align:left}thead th{position:sticky;top:0;background:var(--panel);color:var(--muted);font-size:.78rem;white-space:nowrap}tbody tr:last-child th,tbody tr:last-child td{border-bottom:0}th.number,td.number{text-align:right}.heat{font-weight:650}.positive.heat-1{background:rgba(37,155,112,.08)}.positive.heat-2{background:rgba(37,155,112,.13)}.positive.heat-3{background:rgba(37,155,112,.18)}.positive.heat-4{background:rgba(37,155,112,.24)}.positive.heat-5{background:rgba(37,155,112,.31)}.negative.heat-1{background:rgba(203,70,62,.08)}.negative.heat-2{background:rgba(203,70,62,.13)}.negative.heat-3{background:rgba(203,70,62,.18)}.negative.heat-4{background:rgba(203,70,62,.24)}.negative.heat-5{background:rgba(203,70,62,.31)}.filters{display:flex;gap:10px;flex-wrap:wrap}.sortable{cursor:pointer;user-select:none}.sortable:focus{outline:2px solid var(--accent);outline-offset:-2px}#tradesTable{min-width:1010px}footer{padding:22px 4px;color:var(--muted);font-size:.86rem}footer h2{color:var(--ink)}code{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--accent);overflow-wrap:anywhere}
        @media(max-width:680px){.page{width:min(100% - 20px,1160px);margin:16px auto 36px}.panel{padding:16px 12px;border-radius:12px}.metrics{grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.metric{padding:11px}.section-heading{align-items:flex-start;flex-direction:column}.select-label{width:100%;justify-content:space-between}.select-label select{flex:1}.filters{width:100%}.filters label{flex:1;min-width:135px;display:grid;gap:4px}.filters select{width:100%}th,td{padding:9px 10px}}
        """;

    private const string Script = """
        (()=>{const chartSelect=document.getElementById('chartSymbol');const groups=[...document.querySelectorAll('[data-chart-series]')];function showChart(){const chosen=chartSelect.value;for(const group of groups)group.style.display=group.dataset.chartSeries===chosen?'':'none'}chartSelect.addEventListener('change',showChart);const symbolFilter=document.getElementById('filterSymbol');const dateFilter=document.getElementById('filterDate');const rows=[...document.querySelectorAll('#tradesTable tbody tr')];function filterRows(){for(const row of rows)row.hidden=(symbolFilter.value&&row.dataset.symbol!==symbolFilter.value)||(dateFilter.value&&row.dataset.date!==dateFilter.value)}symbolFilter.addEventListener('change',filterRows);dateFilter.addEventListener('change',filterRows);let sortState={key:'exit',direction:-1};function sortBy(header){const key=header.dataset.sort;sortState=sortState.key===key?{key,direction:-sortState.direction}:{key,direction:1};const index=[...header.parentElement.children].indexOf(header);const body=document.querySelector('#tradesTable tbody');rows.sort((a,b)=>{const av=a.children[index].dataset.sortValue??a.children[index].textContent.trim();const bv=b.children[index].dataset.sortValue??b.children[index].textContent.trim();const an=Number(av),bn=Number(bv);const cmp=Number.isFinite(an)&&Number.isFinite(bn)?an-bn:av.localeCompare(bv);return cmp*sortState.direction});for(const row of rows)body.appendChild(row)}for(const header of document.querySelectorAll('#tradesTable th[data-sort]')){header.addEventListener('click',()=>sortBy(header));header.addEventListener('keydown',event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();sortBy(header)}})}})();
        """;

    private sealed record BacktestMetadata(DateOnly StartDate, DateOnly EndDate, string[] Symbols, string StrategyTemplateId, string TickDataBasePath, string TickSource, long TotalTicks);
    private sealed record TradeRecord(string Symbol, DateTime EntryTime, DateTime ExitTime, string Direction, decimal EntryPrice, decimal ExitPrice, long Quantity, decimal GrossPnL, decimal Costs, decimal NetPnL, string Source);
    private sealed record ReportSnapshot(string[] Symbols, DateOnly[] Dates, Dictionary<string, int> FillsBySymbol, TradeRecord[] Trades, long TickCount, string StrategyTemplateId, string TickSource, DateOnly StartDate, DateOnly EndDate);
}
