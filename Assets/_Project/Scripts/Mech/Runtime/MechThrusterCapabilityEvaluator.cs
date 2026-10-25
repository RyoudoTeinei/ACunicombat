using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Builds an invisible loadout probe and evaluates translation capability from the same
    /// model anchors and nozzle package used by combat. This is intended for hangar statistics,
    /// not for per-frame flight control.
    /// </summary>
    public static class MechThrusterCapabilityEvaluator
    {
        private const float DefaultAuxiliaryThrustKn = 600f;
        private const float TorqueWeight = 0.03f;
        private const int AllocatorIterations = 32;

        public static bool TryApplyToStats(
            MechLoadoutDefinition loadout,
            MechAggregatedStats stats)
        {
            if (loadout == null || stats == null || stats.totalMassKg <= Mathf.Epsilon)
            {
                return false;
            }

            GameObject probeRoot = null;
            try
            {
                probeRoot = new GameObject("__HangarThrusterCapabilityProbe")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                probeRoot.transform.position = new Vector3(0f, -100000f, 0f);

                var assembler = probeRoot.AddComponent<MechAssembler>();
                assembler.ConfigureRuntimePreview();
                assembler.Loadout = loadout;
                assembler.Assemble();
                EnsureMirroredRightForearmNozzle(probeRoot);

                if (!TryCollectNozzles(probeRoot, stats, out var nozzles, out var centerOfMass,
                        out var referenceArmMeters) || nozzles.Count == 0)
                {
                    return false;
                }

                var root = probeRoot.transform;
                var forwardKn = EvaluateAxis(
                    root.forward,
                    true,
                    nozzles,
                    centerOfMass,
                    referenceArmMeters);
                var reverseKn = EvaluateAxis(
                    -root.forward,
                    false,
                    nozzles,
                    centerOfMass,
                    referenceArmMeters);
                var rightKn = EvaluateAxis(
                    root.right,
                    false,
                    nozzles,
                    centerOfMass,
                    referenceArmMeters);
                var leftKn = EvaluateAxis(
                    -root.right,
                    false,
                    nozzles,
                    centerOfMass,
                    referenceArmMeters);
                var upwardKn = EvaluateAxis(
                    root.up,
                    false,
                    nozzles,
                    centerOfMass,
                    referenceArmMeters);
                var downwardKn = EvaluateAxis(
                    -root.up,
                    false,
                    nozzles,
                    centerOfMass,
                    referenceArmMeters);

                var accelerationScale = 1000f / stats.totalMassKg;
                stats.mainAccelerationMps2 = forwardKn * accelerationScale;
                stats.assaultBoostAccelerationMps2 =
                    stats.mainAccelerationMps2 +
                    (stats.boosterPerformance?.assaultBoostThrustKn ?? 0f) * accelerationScale;
                stats.quickBoostAccelerationMps2 =
                    (stats.boosterPerformance?.quickBoostThrustKn ?? 0f) * accelerationScale;
                stats.lateralAccelerationMps2 = Mathf.Min(leftKn, rightKn) * accelerationScale;
                stats.reverseAccelerationMps2 = reverseKn * accelerationScale;
                stats.upwardAccelerationMps2 = upwardKn * accelerationScale;
                stats.downwardAccelerationMps2 = downwardKn * accelerationScale;
                return true;
            }
            finally
            {
                if (probeRoot != null)
                {
                    probeRoot.SetActive(false);
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(probeRoot);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(probeRoot);
                    }
                }
            }
        }

        private static float EvaluateAxis(
            Vector3 requestedDirection,
            bool enableMainThrusters,
            IReadOnlyList<NozzleSample> nozzles,
            Vector3 centerOfMass,
            float referenceArmMeters)
        {
            var count = nozzles.Count;
            var directions = new Vector3[count];
            var torquePerNewton = new Vector3[count];
            var capacities = new float[count];
            var locked = new bool[count];
            var outputs = new float[count];
            var requestedAxis = requestedDirection.normalized;
            var projectedCapacityNewtons = 0f;

            for (var i = 0; i < count; i++)
            {
                var nozzle = nozzles[i];
                var direction = nozzle.BaseForceDirection;
                if (!nozzle.IsMain && nozzle.VectoringEnabled &&
                    nozzle.MaximumVectorAngleDegrees > 0f)
                {
                    direction = Vector3.RotateTowards(
                        direction,
                        requestedAxis,
                        nozzle.MaximumVectorAngleDegrees * Mathf.Deg2Rad,
                        0f).normalized;
                }

                directions[i] = direction;
                capacities[i] = nozzle.CapacityKn * 1000f;
                torquePerNewton[i] = Vector3.Cross(
                    nozzle.WorldPosition - centerOfMass,
                    direction);
                locked[i] = nozzle.IsMain;
                outputs[i] = nozzle.IsMain && enableMainThrusters
                    ? capacities[i]
                    : 0f;
                projectedCapacityNewtons += capacities[i] * Mathf.Max(
                    0f,
                    Vector3.Dot(direction, requestedAxis));
            }

            if (projectedCapacityNewtons <= Mathf.Epsilon)
            {
                return 0f;
            }

            MechTranslationAllocator.Solve(
                requestedAxis * projectedCapacityNewtons,
                directions,
                torquePerNewton,
                capacities,
                locked,
                outputs,
                AllocatorIterations,
                TorqueWeight,
                referenceArmMeters,
                out var achievedForce,
                out _);
            return Mathf.Max(0f, Vector3.Dot(achievedForce, requestedAxis)) / 1000f;
        }

        private static bool TryCollectNozzles(
            GameObject root,
            MechAggregatedStats stats,
            out List<NozzleSample> nozzles,
            out Vector3 centerOfMass,
            out float referenceArmMeters)
        {
            nozzles = new List<NozzleSample>();
            var configuredBySource = LoadConfiguredNozzles();
            var candidates = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (!MechMainThrusterPlume.TryResolveAuxiliaryNozzleSlot(
                        candidate,
                        out var slot) ||
                    !MechMainThrusterPlume.IsAuxiliaryNozzleAnchor(slot, candidate.name))
                {
                    continue;
                }

                var sourceId = MechMainThrusterPlume.BuildNozzleSourceId(slot, candidate);
                if (configuredBySource.TryGetValue(sourceId, out var configured))
                {
                    AddConfiguredNozzles(root.transform, candidate, slot, sourceId, configured, nozzles);
                }
                else
                {
                    AddDefaultNozzle(root.transform, candidate, slot, nozzles);
                }
            }

            AddMainThrusters(root, stats.mainThrustKn, nozzles);
            centerOfMass = ResolveCenterOfMass(root, out var allBounds);
            referenceArmMeters = Mathf.Max(1f, allBounds.extents.magnitude);
            return nozzles.Count > 0;
        }

        private static void AddConfiguredNozzles(
            Transform vehicleRoot,
            Transform anchor,
            MechPartSlot slot,
            string sourceId,
            IReadOnlyList<MechThrusterNozzleLayoutEntry> configured,
            ICollection<NozzleSample> target)
        {
            MechThrusterNozzleLayoutEntry primary = null;
            var primaryConfigured = false;
            for (var i = 0; i < configured.Count; i++)
            {
                var entry = configured[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.id))
                {
                    continue;
                }

                if (string.Equals(entry.id, sourceId, StringComparison.Ordinal))
                {
                    primaryConfigured = true;
                    if (entry.enabled)
                    {
                        primary = entry;
                    }
                    continue;
                }

                if (entry.enabled)
                {
                    AddConfiguredNozzle(anchor, entry, target);
                }
            }

            if (primary != null)
            {
                AddConfiguredNozzle(anchor, primary, target);
            }
            else if (!primaryConfigured)
            {
                AddDefaultNozzle(vehicleRoot, anchor, slot, target);
            }
        }

        private static void AddConfiguredNozzle(
            Transform anchor,
            MechThrusterNozzleLayoutEntry entry,
            ICollection<NozzleSample> target)
        {
            entry.NormalizePhysicalSettings();
            var exhaustDirection = (anchor.rotation *
                                    Quaternion.Euler(entry.localEulerOffset) *
                                    Vector3.forward).normalized;
            target.Add(new NozzleSample(
                anchor.TransformPoint(entry.localPositionOffset),
                -exhaustDirection,
                entry.ratedThrustKn,
                entry.vectoringEnabled,
                entry.maximumVectorAngleDegrees,
                false));
        }

        private static void AddDefaultNozzle(
            Transform vehicleRoot,
            Transform anchor,
            MechPartSlot slot,
            ICollection<NozzleSample> target)
        {
            var exhaustDirection = ResolveDefaultExhaustDirection(vehicleRoot, anchor, slot);
            target.Add(new NozzleSample(
                anchor.position,
                -exhaustDirection,
                DefaultAuxiliaryThrustKn,
                false,
                0f,
                false));
        }

        private static Vector3 ResolveDefaultExhaustDirection(
            Transform vehicleRoot,
            Transform anchor,
            MechPartSlot slot)
        {
            if (slot != MechPartSlot.Arms &&
                slot != MechPartSlot.LeftArm &&
                slot != MechPartSlot.RightArm)
            {
                return anchor.forward.normalized;
            }

            if (anchor.name.EndsWith("_dmy_7", StringComparison.OrdinalIgnoreCase) ||
                anchor.name.EndsWith("_dmy_8", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveOutwardDirection(vehicleRoot, anchor);
            }

            if (!anchor.name.EndsWith("_dmy_70", StringComparison.OrdinalIgnoreCase) &&
                !anchor.name.EndsWith("_dmy_71", StringComparison.OrdinalIgnoreCase))
            {
                return anchor.forward.normalized;
            }

            var outward = ResolveOutwardDirection(vehicleRoot, anchor);
            var forwardPosition = Vector3.Dot(
                anchor.position - vehicleRoot.position,
                vehicleRoot.forward);
            var matching = vehicleRoot.GetComponentsInChildren<Transform>(true);
            var sum = 0f;
            var count = 0;
            for (var i = 0; i < matching.Length; i++)
            {
                if (!matching[i].name.Equals(anchor.name, StringComparison.OrdinalIgnoreCase) ||
                    !MechMainThrusterPlume.TryResolveAuxiliaryNozzleSlot(
                        matching[i],
                        out var candidateSlot) ||
                    candidateSlot != slot)
                {
                    continue;
                }

                sum += Vector3.Dot(
                    matching[i].position - vehicleRoot.position,
                    vehicleRoot.forward);
                count++;
            }

            var forwardSign = count < 2 || forwardPosition >= sum / count ? 1f : -1f;
            return (outward + vehicleRoot.forward * (0.28f * forwardSign) -
                    vehicleRoot.up * 0.18f).normalized;
        }

        private static Vector3 ResolveOutwardDirection(Transform vehicleRoot, Transform anchor)
        {
            var sideSign = Mathf.Sign(Vector3.Dot(
                anchor.position - vehicleRoot.position,
                vehicleRoot.right));
            if (Mathf.Abs(sideSign) <= Mathf.Epsilon)
            {
                sideSign = anchor.name.EndsWith("_dmy_8", StringComparison.OrdinalIgnoreCase)
                    ? 1f
                    : -1f;
            }

            return vehicleRoot.right * sideSign;
        }

        private static void AddMainThrusters(
            GameObject root,
            float totalMainThrustKn,
            ICollection<NozzleSample> target)
        {
            if (totalMainThrustKn <= Mathf.Epsilon)
            {
                return;
            }

            var anchors = new List<Transform>();
            var candidates = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].name.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase) &&
                    IsUnderBoosterVisual(candidates[i]))
                {
                    anchors.Add(candidates[i]);
                }
            }

            if (anchors.Count == 0)
            {
                return;
            }

            var capacityPerNozzleKn = totalMainThrustKn / anchors.Count;
            for (var i = 0; i < anchors.Count; i++)
            {
                target.Add(new NozzleSample(
                    anchors[i].position,
                    root.transform.forward,
                    capacityPerNozzleKn,
                    false,
                    0f,
                    true));
            }
        }

        private static bool IsUnderBoosterVisual(Transform candidate)
        {
            for (var current = candidate; current != null; current = current.parent)
            {
                var part = current.GetComponent<MechAssembledPart>();
                if (part != null)
                {
                    return part.Slot == MechPartSlot.Booster;
                }

                if (current.name.StartsWith("Booster_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, List<MechThrusterNozzleLayoutEntry>>
            LoadConfiguredNozzles()
        {
            var result = new Dictionary<string, List<MechThrusterNozzleLayoutEntry>>(
                StringComparer.Ordinal);
            var asset = Resources.Load<TextAsset>(MechMainThrusterPlume.NozzleLayoutResourcePath);
            var layout = asset != null
                ? JsonUtility.FromJson<MechThrusterNozzleLayoutData>(asset.text)
                : new MechThrusterNozzleLayoutData();
            layout ??= new MechThrusterNozzleLayoutData();
            layout.NormalizePackageMetadata();
            for (var i = 0; i < layout.nozzles.Count; i++)
            {
                var entry = layout.nozzles[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.sourceId))
                {
                    continue;
                }

                if (!result.TryGetValue(entry.sourceId, out var entries))
                {
                    entries = new List<MechThrusterNozzleLayoutEntry>();
                    result.Add(entry.sourceId, entries);
                }

                entries.Add(entry);
            }

            return result;
        }

        private static Vector3 ResolveCenterOfMass(GameObject root, out Bounds allBounds)
        {
            var state = root.GetComponent<MechRuntimeState>();
            var weightedCenter = Vector3.zero;
            var totalMassKg = 0f;
            var hasBounds = false;
            allBounds = new Bounds(root.transform.position, Vector3.one);
            if (state == null)
            {
                return root.transform.position;
            }

            for (var i = 0; i < state.AssembledParts.Count; i++)
            {
                var record = state.AssembledParts[i];
                if (record?.instance == null)
                {
                    continue;
                }

                if (TryGetExclusivePartBounds(record.instance, out var bounds))
                {
                    if (!hasBounds)
                    {
                        allBounds = bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        allBounds.Encapsulate(bounds);
                    }

                    var massKg = record.part?.Stats != null
                        ? Mathf.Max(0f, record.part.Stats.massKg)
                        : 0f;
                    weightedCenter += bounds.center * massKg;
                    totalMassKg += massKg;
                }
            }

            return totalMassKg > Mathf.Epsilon
                ? weightedCenter / totalMassKg
                : allBounds.center;
        }

        private static bool TryGetExclusivePartBounds(GameObject partRoot, out Bounds bounds)
        {
            var rootPart = partRoot.GetComponent<MechAssembledPart>();
            var renderers = partRoot.GetComponentsInChildren<Renderer>(true);
            var initialized = false;
            bounds = default;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (rootPart != null &&
                    renderers[i].GetComponentInParent<MechAssembledPart>() != rootPart)
                {
                    continue;
                }

                if (!initialized)
                {
                    bounds = renderers[i].bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            return initialized;
        }

        private static void EnsureMirroredRightForearmNozzle(GameObject root)
        {
            const string sourceName = "AM_M_1010_dmy_25";
            const string mirroredName = "AM_M_1010_dmy_25_MIRRORED";
            var parts = root.GetComponentsInChildren<MechAssembledPart>(true);
            for (var partIndex = 0; partIndex < parts.Length; partIndex++)
            {
                var part = parts[partIndex];
                if (part == null ||
                    (part.Slot != MechPartSlot.Arms &&
                     part.Slot != MechPartSlot.LeftArm &&
                     part.Slot != MechPartSlot.RightArm))
                {
                    continue;
                }

                Transform source = null;
                Transform rightForearm = null;
                var descendants = part.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < descendants.Length; i++)
                {
                    var candidate = descendants[i];
                    if (candidate.name == mirroredName)
                    {
                        source = null;
                        rightForearm = null;
                        break;
                    }

                    if (candidate.name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        source = candidate;
                    }
                    else if (candidate.name.Equals("R_ForeArm", StringComparison.OrdinalIgnoreCase))
                    {
                        rightForearm = candidate;
                    }
                }

                if (source == null || rightForearm == null)
                {
                    continue;
                }

                var mirrored = new GameObject(mirroredName).transform;
                mirrored.SetParent(rightForearm, false);
                var sourcePosition = source.localPosition;
                mirrored.localPosition = new Vector3(
                    sourcePosition.x,
                    -sourcePosition.y,
                    sourcePosition.z);
                var sourceRotation = source.localRotation;
                mirrored.localRotation = new Quaternion(
                    sourceRotation.y,
                    sourceRotation.x,
                    sourceRotation.w,
                    sourceRotation.z);
            }
        }

        private readonly struct NozzleSample
        {
            public NozzleSample(
                Vector3 worldPosition,
                Vector3 baseForceDirection,
                float capacityKn,
                bool vectoringEnabled,
                float maximumVectorAngleDegrees,
                bool isMain)
            {
                WorldPosition = worldPosition;
                BaseForceDirection = baseForceDirection.normalized;
                CapacityKn = Mathf.Max(0f, capacityKn);
                VectoringEnabled = vectoringEnabled;
                MaximumVectorAngleDegrees = Mathf.Clamp(maximumVectorAngleDegrees, 0f, 89f);
                IsMain = isMain;
            }

            public Vector3 WorldPosition { get; }
            public Vector3 BaseForceDirection { get; }
            public float CapacityKn { get; }
            public bool VectoringEnabled { get; }
            public float MaximumVectorAngleDegrees { get; }
            public bool IsMain { get; }
        }
    }
}
