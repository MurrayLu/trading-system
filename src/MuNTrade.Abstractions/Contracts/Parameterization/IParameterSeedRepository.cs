using MuNTrade.Abstractions.Dtos.Parameterization;

namespace MuNTrade.Abstractions.Contracts.Parameterization;

public interface IParameterSeedRepository
{
    Task<ParameterSeedTableDto> GetActiveSeedTableVersionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ParameterSeedEntryDto>> GetActiveSeedTableAsync(CancellationToken ct = default);
    Task<ParameterSeedEntryDto?> FindByNameAsync(string parameterName, CancellationToken ct = default);
}

