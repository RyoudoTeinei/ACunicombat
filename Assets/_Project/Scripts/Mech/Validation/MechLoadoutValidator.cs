using System.Collections.Generic;

namespace ACunicombat.Mechs
{
    public static class MechLoadoutValidator
    {
        public static MechValidationResult Validate(MechLoadoutDefinition loadout, MechValidationOptions options = null)
        {
            var result = new MechValidationResult();
            Validate(loadout, result, options);
            return result;
        }

        public static void Validate(MechLoadoutDefinition loadout, MechValidationResult result, MechValidationOptions options = null)
        {
            result.Clear();
            if (options == null)
            {
                options = new MechValidationOptions();
            }

            if (loadout == null)
            {
                result.Add(MechValidationSeverity.Error, MechPartSlot.None, "LOADOUT_NULL", "No loadout assigned.");
                return;
            }

            var assignedSlots = new Dictionary<MechPartSlot, MechPartDefinition>();
            var availableMounts = new Dictionary<MechPartSlot, List<MechMountPointDefinition>>();
            var loadoutTags = loadout.BuildLoadoutTagSet();
            var enabledAssignments = new List<MechPartAssignment>();

            foreach (var assignment in loadout.EnumerateEnabledAssignments())
            {
                enabledAssignments.Add(assignment);
                ValidateAssignment(assignment, assignedSlots, result);

                if (assignment.part == null)
                {
                    continue;
                }

                RegisterMounts(assignment.part, availableMounts);
            }

            for (var i = 0; i < enabledAssignments.Count; i++)
            {
                var assignment = enabledAssignments[i];
                if (assignment.part != null)
                {
                    ValidatePartDependencies(assignment.part, assignedSlots, loadoutTags, result);
                }
            }

            ValidateRequiredSlots(assignedSlots, options, result);
            ValidateHardpoints(assignedSlots, availableMounts, options, result);

            var stats = MechStatsCalculator.Calculate(loadout);
            ValidateBudgets(stats, options, result);
        }

        private static void ValidateAssignment(
            MechPartAssignment assignment,
            Dictionary<MechPartSlot, MechPartDefinition> assignedSlots,
            MechValidationResult result)
        {
            if (assignment == null || !assignment.enabled)
            {
                return;
            }

            if (assignment.slot == MechPartSlot.None)
            {
                result.Add(MechValidationSeverity.Error, MechPartSlot.None, "SLOT_NONE", "An enabled assignment uses the None slot.");
                return;
            }

            if (assignment.part == null)
            {
                result.Add(MechValidationSeverity.Warning, assignment.slot, "PART_EMPTY", $"Slot {assignment.slot} is enabled but has no part.");
                return;
            }

            if (assignedSlots.ContainsKey(assignment.slot))
            {
                result.Add(MechValidationSeverity.Error, assignment.slot, "DUPLICATE_SLOT", $"Slot {assignment.slot} has more than one enabled part.");
            }
            else
            {
                assignedSlots.Add(assignment.slot, assignment.part);
            }

            if (assignment.part.Slot != assignment.slot)
            {
                var compatibleMirroredArm =
                    assignment.part.Slot == MechPartSlot.Arms &&
                    MechSlotRules.IsMirroredArmSlot(assignment.slot);

                if (!compatibleMirroredArm)
                {
                    result.Add(
                        MechValidationSeverity.Error,
                        assignment.slot,
                        "SLOT_MISMATCH",
                        $"Part '{assignment.part.DisplayName}' is a {assignment.part.Slot} part but is assigned to {assignment.slot}.");
                }
            }
        }

        private static void RegisterMounts(
            MechPartDefinition part,
            Dictionary<MechPartSlot, List<MechMountPointDefinition>> availableMounts)
        {
            var mounts = part.MountPoints;
            for (var i = 0; i < mounts.Count; i++)
            {
                var mount = mounts[i];
                if (mount == null || mount.targetSlot == MechPartSlot.None)
                {
                    continue;
                }

                if (!availableMounts.TryGetValue(mount.targetSlot, out var list))
                {
                    list = new List<MechMountPointDefinition>();
                    availableMounts.Add(mount.targetSlot, list);
                }

                list.Add(mount);
            }
        }

        private static void ValidatePartDependencies(
            MechPartDefinition part,
            Dictionary<MechPartSlot, MechPartDefinition> assignedSlots,
            HashSet<string> loadoutTags,
            MechValidationResult result)
        {
            var requiredSlots = part.RequiredInstalledSlots;
            for (var i = 0; i < requiredSlots.Count; i++)
            {
                if (!assignedSlots.ContainsKey(requiredSlots[i]))
                {
                    result.Add(
                        MechValidationSeverity.Error,
                        part.Slot,
                        "MISSING_DEPENDENCY_SLOT",
                        $"Part '{part.DisplayName}' requires slot {requiredSlots[i]} to be installed.");
                }
            }

            var requiredTags = part.RequiredLoadoutTags;
            for (var i = 0; i < requiredTags.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(requiredTags[i]) && !loadoutTags.Contains(requiredTags[i]))
                {
                    result.Add(
                        MechValidationSeverity.Error,
                        part.Slot,
                        "MISSING_DEPENDENCY_TAG",
                        $"Part '{part.DisplayName}' requires a loadout tag '{requiredTags[i]}'.");
                }
            }

            var blockedTags = part.BlockedLoadoutTags;
            for (var i = 0; i < blockedTags.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(blockedTags[i]) && loadoutTags.Contains(blockedTags[i]))
                {
                    result.Add(
                        MechValidationSeverity.Error,
                        part.Slot,
                        "BLOCKED_TAG",
                        $"Part '{part.DisplayName}' is incompatible with loadout tag '{blockedTags[i]}'.");
                }
            }
        }

        private static void ValidateRequiredSlots(
            Dictionary<MechPartSlot, MechPartDefinition> assignedSlots,
            MechValidationOptions options,
            MechValidationResult result)
        {
            foreach (var slot in options.EnumerateRequiredSlots())
            {
                if (assignedSlots.ContainsKey(slot))
                {
                    continue;
                }

                if (options.allowArmsSlotInsteadOfSeparateArms &&
                    (slot == MechPartSlot.LeftArm || slot == MechPartSlot.RightArm) &&
                    assignedSlots.ContainsKey(MechPartSlot.Arms))
                {
                    continue;
                }

                result.Add(MechValidationSeverity.Error, slot, "REQUIRED_SLOT_EMPTY", $"Required slot {slot} is empty.");
            }
        }

        private static void ValidateHardpoints(
            Dictionary<MechPartSlot, MechPartDefinition> assignedSlots,
            Dictionary<MechPartSlot, List<MechMountPointDefinition>> availableMounts,
            MechValidationOptions options,
            MechValidationResult result)
        {
            if (!options.requireWeaponHardpoints)
            {
                return;
            }

            foreach (var pair in assignedSlots)
            {
                var slot = pair.Key;
                if (!MechSlotRules.IsWeaponSlot(slot))
                {
                    continue;
                }

                if (!availableMounts.TryGetValue(slot, out var mounts) || mounts.Count == 0)
                {
                    result.Add(MechValidationSeverity.Warning, slot, "MISSING_HARDPOINT", $"Weapon slot {slot} has no matching hardpoint mount.");
                    continue;
                }

                var partMass = pair.Value != null ? pair.Value.Stats.massKg : 0f;
                var supported = false;
                for (var i = 0; i < mounts.Count; i++)
                {
                    if (mounts[i].maxMassKg <= 0f || partMass <= mounts[i].maxMassKg)
                    {
                        supported = true;
                        break;
                    }
                }

                if (!supported)
                {
                    result.Add(MechValidationSeverity.Error, slot, "HARDPOINT_OVERWEIGHT", $"Weapon slot {slot} exceeds all matching hardpoint mass limits.");
                }
            }
        }

        private static void ValidateBudgets(MechAggregatedStats stats, MechValidationOptions options, MechValidationResult result)
        {
            if (options.enforceSupportedMass && stats.supportedMassKg > 0f && stats.totalMassKg > stats.supportedMassKg)
            {
                result.Add(
                    MechValidationSeverity.Warning,
                    MechPartSlot.Legs,
                    "SUPPORTED_MASS_EXCEEDED",
                    $"Total mass {stats.totalMassKg:0} kg exceeds supported mass {stats.supportedMassKg:0} kg.");
            }

            if (options.enforcePowerBudget && stats.powerOutputMw > 0f && stats.peakPowerSurplusMw < 0f)
            {
                result.Add(
                    MechValidationSeverity.Warning,
                    MechPartSlot.Generator,
                    "POWER_BUDGET_NEGATIVE",
                    $"Peak power draw exceeds generator output by {-stats.peakPowerSurplusMw * 1000f:0.#} kW.");
            }

            if (options.enforceHeatBudget && stats.heatDissipationMw > 0f && stats.heatSurplusMw < 0f)
            {
                result.Add(
                    MechValidationSeverity.Warning,
                    MechPartSlot.Radiator,
                    "HEAT_BUDGET_NEGATIVE",
                    $"Heat generation exceeds dissipation by {-stats.heatSurplusMw * 1000f:0.#} kW.");
            }
        }
    }
}
