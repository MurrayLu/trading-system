using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using MuNTrade.Abstractions.Contracts.Parameterization;
using MuNTrade.Abstractions.Domain.Parameterization;
using MuNTrade.Abstractions.Dtos.Parameterization;

namespace MuNTrade.Infrastructure.Storage.Parameterization;

public sealed class JsonParameterWhitelistRegistry : IParameterWhitelistRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _liveEnabledPath;
    private readonly string _researchOnlyPath;

    public JsonParameterWhitelistRegistry(IConfiguration configuration)
    {
        _liveEnabledPath = JsonGovernanceStoragePaths.ResolveLiveEnabledWhitelist(configuration);
        _researchOnlyPath = JsonGovernanceStoragePaths.ResolveResearchOnlyWhitelist(configuration);
    }

    public async Task<IReadOnlyList<ParameterWhitelistEntryDto>> GetAllAsync(CancellationToken ct = default)
    {
        var liveEnabled = await GetLiveEnabledAsync(ct);
        var researchOnly = await GetResearchOnlyAsync(ct);
        return liveEnabled.Concat(researchOnly).ToList();
    }

    public Task<IReadOnlyList<ParameterWhitelistEntryDto>> GetLiveEnabledAsync(CancellationToken ct = default)
    {
        return ReadFileAsync(_liveEnabledPath, ct);
    }

    public Task<IReadOnlyList<ParameterWhitelistEntryDto>> GetResearchOnlyAsync(CancellationToken ct = default)
    {
        return ReadFileAsync(_researchOnlyPath, ct);
    }

    public async Task<ParameterWhitelistEntryDto?> FindByNameAsync(string parameterName, CancellationToken ct = default)
    {
        var entries = await GetAllAsync(ct);
        return entries.FirstOrDefault(x => string.Equals(x.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ParameterWhitelistEntryDto>> QueryAsync(
        LayerOwner? layerOwner = null,
        ParameterFamily? family = null,
        CancellationToken ct = default)
    {
        var entries = await GetAllAsync(ct);
        return entries
            .Where(x => !layerOwner.HasValue || x.LayerOwner == layerOwner.Value)
            .Where(x => !family.HasValue || x.Family == family.Value)
            .ToList();
    }

    private static async Task<IReadOnlyList<ParameterWhitelistEntryDto>> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return Array.Empty<ParameterWhitelistEntryDto>();

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var entries = await JsonSerializer.DeserializeAsync<List<ParameterWhitelistEntryDto>>(stream, JsonOptions, ct)
            ?? new List<ParameterWhitelistEntryDto>();

        var invalid = entries
            .Select(Validate)
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        if (invalid.Count > 0)
            throw new InvalidDataException($"Whitelist validation failed in file '{Path.GetFileName(path)}': {string.Join(" | ", invalid)}");

        return entries;
    }

    private static string? Validate(ParameterWhitelistEntryDto entry)
    {
        var missing = new List<string>();
        AddIfMissing(missing, nameof(entry.ParameterName), entry.ParameterName);
        AddIfMissing(missing, nameof(entry.DefaultValue), entry.DefaultValue);
        AddIfMissing(missing, nameof(entry.WhyThisRange), entry.WhyThisRange);
        AddIfMissing(missing, nameof(entry.OverfitRiskNote), entry.OverfitRiskNote);
        if (entry.Family == ParameterFamily.Unknown) missing.Add(nameof(entry.Family));
        if (entry.LayerOwner == LayerOwner.Unknown) missing.Add(nameof(entry.LayerOwner));
        if (missing.Count == 0)
            return null;

        return $"parameter '{entry.ParameterName}' is missing/invalid [{string.Join(", ", missing)}]";
    }

    private static void AddIfMissing(List<string> missing, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            missing.Add(fieldName);
    }
}

