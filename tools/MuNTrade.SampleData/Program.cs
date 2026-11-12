using System.Globalization;
using System.IO.Compression;
using System.Text;

return SampleDataGenerator.Run(args);

public static class SampleDataGenerator
{
    private const uint Seed = 0x5A17C0DE;
    private const int MinutesPerSession = 270;
    private const int OpeningAndClosingTicksPerMinute = 19;
    private const int MidSessionTicksPerMinute = 14;
    private const int WarmupBars = 120;
    private const decimal TickSize = 0.05m;
    private static readonly string[] Symbols = ["9901", "9902", "9903", "9904", "9905"];
    private static readonly DateOnly[] TradeDates =
    [
        new(2024, 3, 11), new(2024, 3, 12), new(2024, 3, 13), new(2024, 3, 14), new(2024, 3, 15)
    ];
    private static readonly DateOnly WarmupDate = new(2024, 3, 8);

    public static int Run(string[] args)
    {
        try
        {
            var root = FindRepositoryRoot();
            var output = ParseOutputPath(args, root);
            Generate(output);

            var tickCount = Symbols.Length * TradeDates.Length * TicksPerDay;
            var totalBytes = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            Console.WriteLine(FormattableString.Invariant($"Generated deterministic synthetic data: {Symbols.Length} symbols × {TradeDates.Length} trading days × {TicksPerDay:N0} ticks/day = {tickCount:N0} ticks."));
            Console.WriteLine(FormattableString.Invariant($"Warmup: {WarmupBars} one-minute bars per symbol on {WarmupDate:yyyy-MM-dd}; daily K lines included for each tick date."));
            Console.WriteLine(FormattableString.Invariant($"Output: {output} ({totalBytes:N0} bytes)"));
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("用法：dotnet run --project tools/MuNTrade.SampleData -c Release [--out <dir>]");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"樣本資料產生失敗：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static int TicksPerDay => (OpeningAndClosingTicksPerMinute * 60) + (MidSessionTicksPerMinute * (MinutesPerSession - 60));

    public static void Generate(string output)
    {
        output = Path.GetFullPath(output);
        var tickRoot = Path.Combine(output, "ticks");
        var klineRoot = Path.Combine(output, "klines");
        Directory.CreateDirectory(tickRoot);
        Directory.CreateDirectory(klineRoot);

        foreach (var (symbol, symbolIndex) in Symbols.Select((value, index) => (value, index)))
        {
            var symbolTicks = Path.Combine(tickRoot, symbol);
            var symbolKLines = Path.Combine(klineRoot, symbol);
            Directory.CreateDirectory(symbolTicks);
            Directory.CreateDirectory(symbolKLines);

            WriteWarmupKLines(symbolKLines, symbolIndex);
            for (var dayIndex = 0; dayIndex < TradeDates.Length; dayIndex++)
            {
                WriteTicks(symbolTicks, symbolKLines, symbol, symbolIndex, dayIndex, TradeDates[dayIndex]);
            }
        }

        File.WriteAllText(Path.Combine(output, "holidays.txt"), "# Synthetic sample calendar; weekends are handled by the reader.\n", new UTF8Encoding(false));
    }

    private static string ParseOutputPath(string[] args, string root)
    {
        if (args.Length == 0) return Path.Combine(root, "samples");
        if (args.Length == 2 && args[0] == "--out")
            return Path.GetFullPath(args[1], root);
        throw new ArgumentException("只接受選用參數 --out <dir>。");
    }

    private static void WriteTicks(string directory, string klineDirectory, string symbol, int symbolIndex, int dayIndex, DateOnly date)
    {
        var path = Path.Combine(directory, $"{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_D_{symbol}.csv.gz");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.NewLine = "\n";
        writer.WriteLine("Timestamp,Symbol,Market,Price,Volume");

        var random = new DeterministicRandom(MakeSeed(symbolIndex, dayIndex));
        var records = new List<KLineRecord>(MinutesPerSession);
        var noise = 0;
        for (var minute = 0; minute < MinutesPerSession; minute++)
        {
            var ticksThisMinute = minute < 30 || minute >= 240
                ? OpeningAndClosingTicksPerMinute
                : MidSessionTicksPerMinute;
            var segmentWidth = 60_000 / ticksThisMinute;
            var minuteStart = date.ToDateTime(new TimeOnly(9, 0)).AddMinutes(minute);
            decimal open = 0m;
            decimal high = decimal.MinValue;
            decimal low = decimal.MaxValue;
            decimal close = 0m;
            long totalVolume = 0;

            for (var tick = 0; tick < ticksThisMinute; tick++)
            {
                var offsetMs = (tick * segmentWidth) + random.NextInt(0, segmentWidth);
                var timestamp = minuteStart.AddMilliseconds(offsetMs);
                var step = random.NextInt(0, 8) switch
                {
                    0 => -2,
                    1 => -1,
                    2 => -1,
                    3 => 0,
                    4 => 0,
                    5 => 0,
                    6 => 1,
                    _ => 2
                };
                noise = Math.Clamp(noise + step, -36, 36);
                var priceTicks = noise + MotifTicks(symbolIndex, dayIndex, minute);
                var price = BasePrice(symbolIndex) + (priceTicks * TickSize);
                var volume = random.NextInt(1, 61);
                if (random.NextInt(0, 100) == 0)
                    volume = random.NextInt(500, 3001);

                writer.Write(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(symbol);
                writer.Write(",SYNTH,");
                writer.Write(price.ToString("0.00", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.WriteLine(volume.ToString(CultureInfo.InvariantCulture));

                if (tick == 0) open = price;
                high = Math.Max(high, price);
                low = Math.Min(low, price);
                close = price;
                totalVolume += volume;
            }

            records.Add(new KLineRecord(minuteStart, open, high, low, close, totalVolume));
        }

        WriteKLineFile(klineDirectory, date, records);
    }

    private static void WriteWarmupKLines(string directory, int symbolIndex)
    {
        var records = new List<KLineRecord>(WarmupBars);
        var random = new DeterministicRandom(MakeSeed(symbolIndex, 99));
        var level = 0;
        for (var i = 0; i < WarmupBars; i++)
        {
            level = Math.Clamp(level + random.NextInt(-2, 3), -10, 10);
            var price = BasePrice(symbolIndex) + (level * TickSize);
            records.Add(new KLineRecord(WarmupDate.ToDateTime(new TimeOnly(9, 0)).AddMinutes(i), price, price, price, price, 1));
        }
        WriteKLineFile(directory, WarmupDate, records);
    }

    private static void WriteKLineFile(string directory, DateOnly date, IReadOnlyList<KLineRecord> records)
    {
        var dateText = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"1 分 K 線_{dateText}_{dateText}.json");
        var json = System.Text.Json.JsonSerializer.Serialize(records, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        json = json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }


    private static int MotifTicks(int symbolIndex, int dayIndex, int minute)
    {
        var phase = (minute + (dayIndex * 11) + (symbolIndex * 7)) % MinutesPerSession;
        return symbolIndex switch
        {
            0 => (int)Math.Round(Math.Sin((phase * 2d * Math.PI) / 54d) * 12d),
            1 => (dayIndex % 2 == 0 ? 1 : -1) * (phase * 36 / (MinutesPerSession - 1)),
            2 => SharpRiseAndDrop(phase),
            3 => FalseBreakout(phase),
            _ => DownThenRecover(phase, dayIndex)
        };
    }

    private static int SharpRiseAndDrop(int minute)
    {
        if (minute < 70) return 0;
        if (minute < 115) return (minute - 70) * 48 / 45;
        if (minute < 170) return 48 - (minute - 115) * 66 / 55;
        if (minute < 220) return -18 + (int)Math.Round(Math.Sin((minute - 170) * 2d * Math.PI / 28d) * 4d);
        return -18 + ((minute - 220) * 18 / 50);
    }

    private static int FalseBreakout(int minute)
    {
        if (minute < 80) return (int)Math.Round(Math.Sin(minute * 2d * Math.PI / 45d) * 7d);
        if (minute < 98) return (minute - 80) * 28 / 18;
        if (minute < 116) return 28 - ((minute - 98) * 31 / 18);
        if (minute < 190) return (int)Math.Round(Math.Sin((minute - 116) * 2d * Math.PI / 38d) * 6d);
        if (minute < 208) return -((minute - 190) * 24 / 18);
        if (minute < 226) return -24 + ((minute - 208) * 24 / 18);
        return 0;
    }

    private static int DownThenRecover(int minute, int dayIndex)
    {
        var direction = dayIndex % 2 == 0 ? -1 : 1;
        if (minute < 150) return direction * (minute * 34 / 150);
        return direction * 34 - (direction * (minute - 150) * 24 / (MinutesPerSession - 150));
    }

    private static decimal BasePrice(int symbolIndex) => 30m + (symbolIndex * 2m);

    private static uint MakeSeed(int symbolIndex, int dayIndex)
    {
        var seed = Seed ^ ((uint)symbolIndex * 0x9E3779B9u) ^ ((uint)dayIndex * 0x85EBCA6Bu);
        return seed == 0 ? 1u : seed;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "MuNTrade.sln"))) return current.FullName;
                current = current.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }

    private sealed record KLineRecord(DateTime KlineTime, decimal Open, decimal High, decimal Low, decimal Close, long Qty);

    private struct DeterministicRandom(uint state)
    {
        private uint _state = state;

        public int NextInt(int minInclusive, int maxExclusive)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return minInclusive + (int)(_state % (uint)(maxExclusive - minInclusive));
        }
    }
}
