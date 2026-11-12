using MuNTrade.Abstractions.Dtos.Parameterization;

namespace MuNTrade.Abstractions.Contracts.Parameterization;

public interface IParameterBlacklistRegistry
{
    Task<IReadOnlyList<ParameterBlacklistReferenceDto>> GetAllAsync(CancellationToken ct = default);
    Task<ParameterBlacklistReferenceDto?> FindByNameAsync(string name, CancellationToken ct = default);
}

