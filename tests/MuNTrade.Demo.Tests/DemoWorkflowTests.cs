using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

public sealed class DemoWorkflowTests
{
    [Fact]
    public void SampleDataGenerator_ProducesIdenticalHashesForSameSeed()
    {
        var first = CreateTempDirectory();
        var second = CreateTempDirectory();
        try
        {
            SampleDataGenerator.Generate(first);
            SampleDataGenerator.Generate(second);

            var firstHashes = HashFilesByContent(first);
            var secondHashes = HashFilesByContent(second);
            var committedHashes = HashFilesByContent(Path.Combine(FindRepositoryRoot(), "samples"), validateTextFormat: false);
            Assert.Equal(firstHashes.ToArray(), secondHashes.ToArray());
            foreach (var (relativePath, hash) in firstHashes)
            {
                Assert.True(committedHashes.TryGetValue(relativePath, out var committedHash), $"Committed sample is missing: {relativePath}");
                Assert.Equal(hash, committedHash);
            }
            AssertSampleTickQuality(first);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_BacktestsOneSymbolAndWritesFillsAndSummary()
    {
        var output = CreateTempDirectory();
        try
        {
            var run = await RunCliAsync(
                "backtest", "--symbol", "9901", "--from", "2024-03-11", "--to", "2024-03-11", "--out", output);

            Assert.True(run.ExitCode == 0, $"CLI exit code: {run.ExitCode}\n{run.StandardOutput}\n{run.StandardError}");
            var summary = Path.Combine(output, "backtest_summary.csv");
            Assert.True(File.Exists(summary), $"Expected summary was not written.\n{run.StandardOutput}\n{run.StandardError}");
            Assert.True(CountFills(output) > 0, $"No fill events were written.\n{run.StandardOutput}\n{run.StandardError}");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_ExitsNonZeroWhenSampleDataIsMissing()
    {
        var output = CreateTempDirectory();
        try
        {
            var run = await RunCliAsync(
                "backtest", "--symbol", "9901", "--from", "2024-03-25", "--to", "2024-03-25", "--out", output);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("找不到指定日期與商品的樣本", run.StandardError + run.StandardOutput);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_ReportIsSelfContainedAndDeterministic()
    {
        var output = CreateTempDirectory();
        var site = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileName(output) + "-site");
        try
        {
            var backtest = await RunCliAsync(
                "backtest", "--symbol", "9901", "--from", "2024-03-11", "--to", "2024-03-11", "--out", output);
            Assert.True(backtest.ExitCode == 0, $"CLI exit code: {backtest.ExitCode}\n{backtest.StandardOutput}\n{backtest.StandardError}");

            var first = await RunCliAsync("report", "--in", output, "--site", site);
            Assert.True(first.ExitCode == 0, $"Report exit code: {first.ExitCode}\n{first.StandardOutput}\n{first.StandardError}");
            var page = Path.Combine(site, "index.html");
            Assert.True(File.Exists(page));
            var firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(page)));
            var html = await File.ReadAllTextAsync(page);
            Assert.DoesNotContain("\r", html);
            Assert.Contains("總覽 Overview", html);
            Assert.Contains("累計淨損益曲線 Cumulative Net P&amp;L", html);
            Assert.Contains("商品 × 交易日淨損益 Product × Day Net P&amp;L", html);
            Assert.Contains("商品摘要 Product Summary", html);
            Assert.Contains("完成交易明細 Completed Trades", html);
            Assert.DoesNotContain("委託 Orders", html);
            Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);

            var second = await RunCliAsync("report", "--in", output, "--site", site);
            Assert.True(second.ExitCode == 0, $"Second report exit code: {second.ExitCode}\n{second.StandardOutput}\n{second.StandardError}");
            var secondHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(page)));
            Assert.Equal(firstHash, secondHash);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            if (Directory.Exists(site)) Directory.Delete(site, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_ReportExitsNonZeroWhenInputDirectoryDoesNotExist()
    {
        var root = CreateTempDirectory();
        try
        {
            var missing = Path.Combine(root, "missing");
            var site = Path.Combine(root, "site");
            var run = await RunCliAsync("report", "--in", missing, "--site", site);
            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("不存在", run.StandardError + run.StandardOutput);

            var invalid = await RunCliAsync("report", "--in", root, "--site", site);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Contains("不是有效的 MuNTrade.Demo.Cli 回測輸出目錄", invalid.StandardError + invalid.StandardOutput);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CliRun> RunCliAsync(params string[] arguments)
    {
        var root = FindRepositoryRoot();
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "MuNTrade.Demo.Cli.dll");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly is missing from test output: {cliAssembly}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var name in startInfo.Environment.Keys.Where(x => x.StartsWith("MUNTRADE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            startInfo.Environment.Remove(name);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the demo CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliRun(process.ExitCode, await stdout, await stderr);
    }

    private static int CountFills(string output)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(output, "*.jsonl", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(path))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("Type", out var type) && type.GetString() == "OrderFillEvent")
                    count++;
            }
        }
        return count;
    }

    private static SortedDictionary<string, string> HashFilesByContent(string directory, bool validateTextFormat = true)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            byte[] content;
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var compressed = File.OpenRead(path);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                gzip.CopyTo(expanded);
                content = expanded.ToArray();
            }
            else
            {
                content = File.ReadAllBytes(path);
                if (validateTextFormat && (Path.GetExtension(path) is ".json" or ".txt"))
                    Assert.DoesNotContain((byte)'\r', content);
            }
            if (validateTextFormat && content.Length >= 3)
                Assert.False(content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF, $"Unexpected UTF-8 BOM in {relative}.");
            if (validateTextFormat && path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                Assert.DoesNotContain((byte)'\r', content);
            hashes[relative] = Convert.ToHexString(SHA256.HashData(content));
        }
        return hashes;
    }

    private static void AssertSampleTickQuality(string directory)
    {
        var files = Directory.EnumerateFiles(Path.Combine(directory, "ticks"), "*.csv.gz", SearchOption.AllDirectories).ToArray();
        Assert.Equal(25, files.Length);

        foreach (var path in files)
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            Assert.Equal("Timestamp,Symbol,Market,Price,Volume", reader.ReadLine());

            var previous = DateTime.MinValue;
            var intervals = new HashSet<long>();
            var minuteCounts = new Dictionary<int, int>();
            var volumes = new HashSet<int>();
            var rows = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var fields = line.Split(',');
                var timestamp = DateTime.ParseExact(fields[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var price = decimal.Parse(fields[3], CultureInfo.InvariantCulture);
                var volume = int.Parse(fields[4], CultureInfo.InvariantCulture);
                var minute = (timestamp.Hour * 60) + timestamp.Minute - (9 * 60);

                if (previous != DateTime.MinValue)
                {
                    Assert.True(timestamp > previous, $"Tick timestamps must increase in {path}.");
                    intervals.Add((timestamp - previous).Ticks);
                }
                previous = timestamp;
                minuteCounts[minute] = minuteCounts.GetValueOrDefault(minute) + 1;
                volumes.Add(volume);
                Assert.True(price > 0m);
                Assert.Equal(decimal.Truncate(price / 0.05m), price / 0.05m);
                Assert.True(volume > 0);
                rows++;
            }

            Assert.Equal(SampleDataGenerator.TicksPerDay, rows);
            Assert.True(intervals.Count > 2, $"Tick intervals should vary in {path}.");
            Assert.True(volumes.Count > 10, $"Tick volumes should vary in {path}.");
            Assert.Contains(minuteCounts, pair => pair.Key < 30 && pair.Value == 19);
            Assert.Contains(minuteCounts, pair => pair.Key is >= 90 and < 180 && pair.Value == 14);
            Assert.Contains(minuteCounts, pair => pair.Key >= 240 && pair.Value == 19);
            Assert.Contains(volumes, volume => volume >= 500);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "muntrade-demo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "MuNTrade.sln"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not find MuNTrade.sln from the test output directory.");
    }

    private sealed record CliRun(int ExitCode, string StandardOutput, string StandardError);
}
