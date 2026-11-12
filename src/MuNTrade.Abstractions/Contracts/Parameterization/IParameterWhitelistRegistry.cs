using MuNTrade.Abstractions.Domain.Parameterization;
using MuNTrade.Abstractions.Dtos.Parameterization;

namespace MuNTrade.Abstractions.Contracts.Parameterization;

public interface IParameterWhitelistRegistry
{
    Task<IReadOnlyList<ParameterWhitelistEntryDto>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ParameterWhitelistEntryDto>> GetLiveEnabledAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ParameterWhitelistEntryDto>> GetResearchOnlyAsync(CancellationToken ct = default);
    Task<ParameterWhitelistEntryDto?> FindByNameAsync(string parameterName, CancellationToken ct = default);
    Task<IReadOnlyList<ParameterWhitelistEntryDto>> QueryAsync(
        LayerOwner? layerOwner = null,
        ParameterFamily? family = null,
        CancellationToken ct = default);
}

