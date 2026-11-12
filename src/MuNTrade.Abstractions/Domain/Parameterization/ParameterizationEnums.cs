namespace MuNTrade.Abstractions.Domain.Parameterization;

public enum ParameterFamily
{
    Unknown = 0,
    Threshold = 1,
    Window = 2,
    Ratio = 3,
    RiskGuard = 4,
    ManagementAmplitude = 5,
    Universe = 6
}

public enum LayerOwner
{
    Unknown = 0,
    ContextBias = 2,
    State = 3,
    Structure = 4,
    RiskFeasibility = 5,
    ExecutionRiskExit = 6,
    PositionSizingStop = 7
}

public enum CouplingGroup
{
    None = 0,
    SameLayer = 1,
    LimitedCrossLayer = 2,
    ManualOnly = 3
}

public enum DataDependency
{
    ObservableLive = 0,
    ObservableReplay = 1,
    DerivedOnly = 2,
    ResearchOnly = 3
}

public enum SearchStage
{
    Stage0SeedOnly = 0,
    Stage1SingleLayer = 1,
    Stage2LimitedCoupling = 2,
    Stage3ExtendedSearch = 3,
    Stage4ReviewPending = 4,
    Stage5Frozen = 5
}

public enum PromotionStage
{
    NotEligible = 0,
    ResearchOnly = 1,
    LiveCandidate = 2,
    LiveEnabled = 3,
    Frozen = 4,
    RolledBack = 5
}

public enum ObjectiveMetric
{
    Unknown = 0,
    Expectancy = 1,
    Drawdown = 2,
    WinRate = 3,
    HoldTime = 4,
    SemanticConsistency = 5,
    PracticalTheoreticalDelta = 6
}

public enum GuardrailStatus
{
    Unknown = 0,
    Pass = 1,
    Warn = 2,
    Fail = 3
}

public enum RejectionStatus
{
    None = 0,
    RejectedByWhitelist = 1,
    RejectedByConstraint = 2,
    RejectedByReview = 3
}

public enum PromotionConstraintStatus
{
    Unknown = 0,
    Pass = 1,
    FailSemanticConsistency = 2,
    FailDataAvailability = 3,
    FailPracticality = 4
}

public enum SemanticConsistencyStatus
{
    Unknown = 0,
    Pass = 1,
    Drifted = 2,
    Failed = 3
}

public enum ParameterEntryAvailability
{
    ResearchOnly = 0,
    LiveEnabled = 1
}

