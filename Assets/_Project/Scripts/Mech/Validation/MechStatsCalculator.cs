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
                MechPartSignatureDefaults.AddFallback(
                    target,
                    assignment.part.Stats,
                    assignment.part.Slot);
                if (assignment.part.Slot == MechPartSlot.Head)
                {
                    target.AddHeadSensorPerformance(assignment.part.HeadSensorPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.Arms)
                {
                    target.AddArmPerformance(assignment.part.ArmPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.Core)
                {
                    target.AddCorePerformance(assignment.part.CorePerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.Legs)
                {
                    target.AddLegPerformance(assignment.part.LegPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.Booster)
                {
                    target.AddBoosterPerformance(assignment.part.BoosterPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.Generator)
                {
                    target.AddGeneratorPerformance(assignment.part.GeneratorPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.Radar)
                {
                    target.AddRadarPerformance(assignment.part.RadarPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.FireControlSystem)
                {
                    target.AddFireControlPerformance(assignment.part.FireControlPerformance);
                }
                else if (assignment.part.Slot == MechPartSlot.DataLinkTerminal)
                {
                    target.AddDataLinkPerformance(assignment.part.DataLinkPerformance);
                }
            }

            target.RecalculateDerived();
        }
    }
}
