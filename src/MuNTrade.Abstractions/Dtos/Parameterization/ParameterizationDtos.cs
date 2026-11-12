using MuNTrade.Abstractions.Domain.Parameterization;

namespace MuNTrade.Abstractions.Dtos.Parameterization;

public sealed record ParameterWhitelistEntryDto
{
    public required string ParameterName { get; init; }
    public required string DefaultValue { get; init; }
    public string? RoughMin { get; init; }
    public string? RoughMax { get; init; }
    public string? WhyThisRange { get; init; }
    public ParameterFamily Family { get; init; }
    public LayerOwner LayerOwner { get; init; }
    public CouplingGroup CouplingGroup { get; init; }
    public DataDependency DataDependency { get; init; }
    public SearchStage SearchStage { get; init; }
    public PromotionStage PromotionStage { get; init; }
    public ParameterEntryAvailability Availability { get; init; }
    public string? OverfitRiskNote { get; init; }
}

public sealed record ParameterBlacklistReferenceDto
{
    public required string Name { get; init; }
    public required string Reason { get; init; }
    public required string CanonicalReference { get; init; }
}

public sealed record ParameterSeedEntryDto
{
    public string? SeedEntryId { get; init; }
    public string? SeedTableVersionId { get; init; }
    public string? ParameterName { get; init; }
    public string? SemanticRole { get; init; }
    public string? ParameterType { get; init; }
    public string? DefaultValue { get; init; }
    public string? ModeScope { get; init; }
    public ParameterFamily ParameterFamily { get; init; }
    public LayerOwner LayerOwner { get; init; }
    public CouplingGroup CouplingGroup { get; init; }
    public DataDependency DataDependency { get; init; }
    public string? RoughMin { get; init; }
    public string? RoughMax { get; init; }
    public string? AllowedRangeOrSet { get; init; }
    public string? WhyThisRange { get; init; }
    public SearchStage SearchStage { get; init; }
    public PromotionStage PromotionStage { get; init; }
    public string? OverfitRisk { get; init; }
    public string? LeakageRisk { get; init; }
    public bool IsFrozenConst { get; init; }
    public string? SourceDoc { get; init; }
    public string? SourceDocVersion { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed record ParameterSeedTableDto
{
    public string? SeedTableVersionId { get; init; }
    public string? SeedScope { get; init; }
    public string? ModeScope { get; init; }
    public IReadOnlyList<ParameterSeedEntryDto> Entries { get; init; } = Array.Empty<ParameterSeedEntryDto>();
    public string? Note { get; init; }
}

