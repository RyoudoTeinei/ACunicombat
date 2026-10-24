namespace ACunicombat.Simulation.Core
{
    /// <summary>Numerical scheduling tolerance, not gameplay reaction or freshness slack.</summary>
    public static class SimulationTime
    {
        public const double DeadlineToleranceSeconds=1e-6;
        public static bool IsDue(double now,double deadline)=>now+DeadlineToleranceSeconds>=deadline;
    }
}
