namespace ACunicombat.Mechs
{
    public static class MechStatsCalculator
    {
        public static MechAggregatedStats Calculate(MechLoadoutDefinition loadout)
        {
            var stats = new MechAggregatedStats();
            Calculate(loadout, stats);
            return stats;
        }

        public static void Calculate(MechLoadoutDefinition loadout, MechAggregatedStats target)
        {
            target.Clear();

            if (loadout == null)
            {
                return;
            }

            foreach (var assignment in loadout.EnumerateEnabledAssignments())
            {
                if (assignment.part == null)
                {
                    continue;
                }

                target.Add(assignment.part.Stats);
            }

            target.RecalculateDerived();
        }
    }
}
