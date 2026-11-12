using System.Globalization;

internal sealed record CliArguments(
    string Command,
    string? Symbol = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Strategy = null,
    string? Out = null,
    string? In = null,
    string? Site = null)
{
    public static CliArguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return new CliArguments("help");
        var command = args[0];
        if (command == "list-strategies")
        {
            if (args.Length != 1) throw new ArgumentException("list-strategies 不接受額外參數。");
            return new CliArguments(command);
        }

        if (command is not ("backtest" or "report")) throw new ArgumentException($"未知子命令：{command}");
        string? symbol = null, strategy = null, output = null, input = null, site = null;
        DateOnly? from = null, to = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 後面需要一個值。");
            var option = args[i++];
            var value = args[i];
            switch (command, option)
            {
                case ("backtest", "--symbol"): symbol = value; break;
                case ("backtest", "--from"): from = ParseDate(value, "--from"); break;
                case ("backtest", "--to"): to = ParseDate(value, "--to"); break;
                case ("backtest", "--strategy"): strategy = value; break;
                case ("backtest", "--out"): output = value; break;
                case ("report", "--in"): input = value; break;
                case ("report", "--site"): site = value; break;
                default: throw new ArgumentException($"{command} 不支援參數：{option}");
            }
        }

        if (command == "report")
        {
            input ??= "out";
            site ??= "site";
        }
        return new CliArguments(command, symbol, from, to, strategy, output, input, site);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run --project src/MuNTrade.Demo.Cli -c Release -- list-strategies");
        Console.WriteLine("  dotnet run --project src/MuNTrade.Demo.Cli -c Release -- backtest [--symbol <id>] [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--strategy <templateId>] [--out <dir>]");
        Console.WriteLine("  dotnet run --project src/MuNTrade.Demo.Cli -c Release -- report [--in out] [--site site]");
    }

    private static DateOnly ParseDate(string? value, string option)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new ArgumentException($"{option} 必須使用 yyyy-MM-dd 格式。");
    }
}
