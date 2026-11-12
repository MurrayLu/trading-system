using System.Text;
using System.Text.Json;

internal static class DemoOutputDirectory
{
    public const string MarkerName = ".muntrade-demo-output";
    public const string ManifestName = ".muntrade-demo-manifest.json";
    public const string MarkerContent = "MuNTrade.Demo.Cli output directory v1";

    public static void Prepare(string outputRoot)
    {
        outputRoot = Path.GetFullPath(outputRoot);
        if (File.Exists(outputRoot)) throw new ArgumentException($"輸出路徑不是目錄：{outputRoot}");
        Directory.CreateDirectory(outputRoot);
        var actualEntries = EnumerateOutputEntries(outputRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var markerEntry = $"F:{MarkerName}";
        var manifestEntry = $"F:{ManifestName}";
        var markerPath = Path.Combine(outputRoot, MarkerName);
        var manifestPath = Path.Combine(outputRoot, ManifestName);

        if (actualEntries.Count == 0)
        {
            File.WriteAllText(markerPath, MarkerContent, new UTF8Encoding(false));
            return;
        }
        if (!File.Exists(markerPath) || !string.Equals(File.ReadAllText(markerPath), MarkerContent, StringComparison.Ordinal))
            throw new ArgumentException($"輸出目錄非空且非本工具上次輸出，拒絕清理：{outputRoot}");
        if (!File.Exists(manifestPath))
        {
            if (actualEntries.SetEquals([markerEntry])) return;
            throw new ArgumentException($"輸出目錄缺少有效的本工具清單，拒絕清理：{outputRoot}");
        }

        string[] previousEntries;
        try { previousEntries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? []; }
        catch (JsonException) { throw new ArgumentException($"輸出目錄清單無法辨識，拒絕清理：{outputRoot}"); }

        var allowedEntries = previousEntries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowedEntries.Add(markerEntry);
        allowedEntries.Add(manifestEntry);
        var unknownEntries = actualEntries.Except(allowedEntries, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknownEntries.Length > 0)
            throw new ArgumentException($"輸出目錄含有不屬於本工具的檔案或資料夾，拒絕清理：{outputRoot}");

        foreach (var entry in previousEntries.Where(actualEntries.Contains).Where(x => x.StartsWith("F:", StringComparison.Ordinal)))
            DeleteOutputFileWithRetry(ResolveOutputEntry(outputRoot, entry[2..]));
        foreach (var entry in previousEntries.Where(actualEntries.Contains).Where(x => x.StartsWith("D:", StringComparison.Ordinal))
                     .OrderByDescending(x => x.Length))
        {
            var directory = ResolveOutputEntry(outputRoot, entry[2..]);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
        File.Delete(manifestPath);
    }

    public static void WriteManifest(string outputRoot)
    {
        outputRoot = Path.GetFullPath(outputRoot);
        var markerPath = Path.Combine(outputRoot, MarkerName);
        if (!File.Exists(markerPath) || !string.Equals(File.ReadAllText(markerPath), MarkerContent, StringComparison.Ordinal)) return;
        var entries = EnumerateOutputEntries(outputRoot)
            .Where(x => !string.Equals(x, $"F:{MarkerName}", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(x, $"F:{ManifestName}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        File.WriteAllText(Path.Combine(outputRoot, ManifestName), JsonSerializer.Serialize(entries), new UTF8Encoding(false));
    }

    private static IEnumerable<string> EnumerateOutputEntries(string outputRoot)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(outputRoot, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"輸出目錄包含連結項目，拒絕清理：{path}");
            var relative = Path.GetRelativePath(outputRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return (Directory.Exists(path) ? "D:" : "F:") + relative;
        }
    }

    private static string ResolveOutputEntry(string outputRoot, string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"輸出清單含有越界路徑，拒絕清理：{relative}");
        return fullPath;
    }

    private static void DeleteOutputFileWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(100); }
        }
    }
}
