using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using MuNTrade.Abstractions.Contracts.Parameterization;
using MuNTrade.Abstractions.Domain.Parameterization;
using MuNTrade.Abstractions.Dtos.Parameterization;

namespace MuNTrade.Infrastructure.Storage.Parameterization;

public sealed class JsonParameterSeedRepository : IParameterSeedRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public JsonParameterSeedRepository(IConfiguration configuration)
    {
        _path = JsonGovernanceStoragePaths.ResolveLayer1SeedTable(configuration);
    }

    public async Task<ParameterSeedTableDto> GetActiveSeedTableVersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return new ParameterSeedTableDto
            {
                SeedTableVersionId = "missing-layer1-seed-table",
                SeedScope = "DaytradeLayer1",
                ModeScope = "DaytradeLayer1",
                Entries = Array.Empty<ParameterSeedEntryDto>(),
                Note = "Layer1 seed table file was not found."
            };

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ParameterSeedTableDto table;
        try
        {
            table = await JsonSerializer.DeserializeAsync<ParameterSeedTableDto>(stream, JsonOptions, ct)
                ?? throw new InvalidDataException($"Layer1 seed table file '{Path.GetFileName(_path)}' could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Layer1 seed validation failed in file '{Path.GetFileName(_path)}'. JSON deserialization error: {ex.Message}",
                ex);
        }

        ValidateLayer1SeedTable(_path, table);
        return table;
    }

    public async Task<IReadOnlyList<ParameterSeedEntryDto>> GetActiveSeedTableAsync(CancellationToken ct = default)
    {
        var table = await GetActiveSeedTableVersionAsync(ct);
        return table.Entries;
    }

    public async Task<ParameterSeedEntryDto?> FindByNameAsync(string parameterName, CancellationToken ct = default)
    {
        var entries = await GetActiveSeedTableAsync(ct);
        return entries.FirstOrDefault(x => string.Equals(x.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateLayer1SeedTable(string path, ParameterSeedTableDto table)
    {
        var invalidEntries = new List<string>();
        var invalidParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidFamilyCount = 0;
        var invalidLayerCount = 0;
        var missingRequiredFieldCount = 0;

        if (string.IsNullOrWhiteSpace(table.SeedTableVersionId))
            invalidEntries.Add($"file '{Path.GetFileName(path)}' is missing SeedTableVersionId.");
        if (string.IsNullOrWhiteSpace(table.ModeScope))
            invalidEntries.Add($"file '{Path.GetFileName(path)}' is missing ModeScope.");

        foreach (var entry in table.Entries)
        {
            var missing = new List<string>();
            AddIfMissing(missing, nameof(entry.SeedEntryId), entry.SeedEntryId);
            AddIfMissing(missing, nameof(entry.SeedTableVersionId), entry.SeedTableVersionId);
            AddIfMissing(missing, nameof(entry.ParameterName), entry.ParameterName);
            AddIfMissing(missing, nameof(entry.SemanticRole), entry.SemanticRole);
            AddIfMissing(missing, nameof(entry.ParameterType), entry.ParameterType);
            AddIfMissing(missing, nameof(entry.DefaultValue), entry.DefaultValue);
            AddIfMissing(missing, nameof(entry.WhyThisRange), entry.WhyThisRange);
            AddIfMissing(missing, nameof(entry.ModeScope), entry.ModeScope);
            AddIfMissing(missing, nameof(entry.CreatedBy), entry.CreatedBy);

            if (entry.SearchStage == SearchStage.Stage0SeedOnly && string.IsNullOrWhiteSpace(entry.ModeScope))
                missing.Add(nameof(entry.ModeScope));
            if (entry.PromotionStage == PromotionStage.NotEligible)
                missing.Add(nameof(entry.PromotionStage));

            if (missing.Count > 0)
            {
                missingRequiredFieldCount += missing.Count;
                invalidParameters.Add(entry.ParameterName ?? "<missing-parameter-name>");
                invalidEntries.Add($"file '{Path.GetFileName(path)}', parameter '{entry.ParameterName}': missing/invalid required fields [{string.Join(", ", missing)}]");
            }

            if (!IsAllowedLayer1Family(entry.ParameterFamily))
            {
                invalidFamilyCount++;
                invalidParameters.Add(entry.ParameterName ?? "<missing-parameter-name>");
                invalidEntries.Add($"file '{Path.GetFileName(path)}', parameter '{entry.ParameterName}': parameter_family '{entry.ParameterFamily}' is outside layer1 scope (allowed: Universe).");
            }

            if (!IsAllowedLayer1Owner(entry.LayerOwner))
            {
                invalidLayerCount++;
                invalidParameters.Add(entry.ParameterName ?? "<missing-parameter-name>");
                invalidEntries.Add($"file '{Path.GetFileName(path)}', parameter '{entry.ParameterName}': layer_owner '{entry.LayerOwner}' is outside layer1 scope.");
            }

            if (!string.Equals(entry.SeedTableVersionId, table.SeedTableVersionId, StringComparison.Ordinal))
            {
                invalidParameters.Add(entry.ParameterName ?? "<missing-parameter-name>");
                invalidEntries.Add($"file '{Path.GetFileName(path)}', parameter '{entry.ParameterName}': seed_table_version_id '{entry.SeedTableVersionId}' does not match table version '{table.SeedTableVersionId}'.");
            }
        }

        if (invalidEntries.Count == 0)
            return;

        var validEntries = Math.Max(0, table.Entries.Count - invalidParameters.Count);
        var summary = $"Summary: TotalEntries={table.Entries.Count}; ValidEntries={validEntries}; InvalidEntries={invalidParameters.Count}; InvalidFamilyCount={invalidFamilyCount}; InvalidLayerCount={invalidLayerCount}; MissingRequiredFieldCount={missingRequiredFieldCount}.";
        throw new InvalidDataException($"Layer1 seed validation failed in file '{Path.GetFileName(path)}'. {summary} {string.Join(" | ", invalidEntries)}");
    }

    private static bool IsAllowedLayer1Family(ParameterFamily family) =>
        family is ParameterFamily.Universe;

    private static bool IsAllowedLayer1Owner(LayerOwner layerOwner) =>
        layerOwner is LayerOwner.ContextBias or LayerOwner.State or LayerOwner.Structure
            or LayerOwner.RiskFeasibility or LayerOwner.ExecutionRiskExit or LayerOwner.PositionSizingStop;

    private static void AddIfMissing(List<string> missing, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            missing.Add(fieldName);
    }
}

