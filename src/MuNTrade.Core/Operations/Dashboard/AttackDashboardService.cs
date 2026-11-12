namespace MuNTrade.Core.Operations.Dashboard
{
    // Compatibility shim retained so shared pipeline constructors do not need to change.
    // The AttackDashboard runtime chain is removed by DI and routing cleanup.
    public sealed class AttackDashboardService
    {
        public void ClearState(string symbol)
        {
        }
    }
}

