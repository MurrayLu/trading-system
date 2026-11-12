using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MuNTrade.Abstractions.Contracts.Parameterization;
using MuNTrade.Abstractions.Dtos.Parameterization;

namespace MuNTrade.Infrastructure.Storage.Parameterization;

public sealed class JsonParameterBlacklistRegistry : IParameterBlacklistRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;

    public JsonParameterBlacklistRegistry(IConfiguration configuration)
    {
        _path = JsonGovernanceStoragePaths.Resolve(
            configuration,
            "DaytradeGovernance:BlacklistPath",
            Path.Combine("Data", "Experimentation", "parameter-blacklist.daytrade.json"));
    }

    public async Task<IReadOnlyList<ParameterBlacklistReferenceDto>> GetAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return Array.Empty<ParameterBlacklistReferenceDto>();

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<List<ParameterBlacklistReferenceDto>>(stream, JsonOptions, ct)
            ?? new List<ParameterBlacklistReferenceDto>();
    }

    public async Task<ParameterBlacklistReferenceDto?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var entries = await GetAllAsync(ct);
        return entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

