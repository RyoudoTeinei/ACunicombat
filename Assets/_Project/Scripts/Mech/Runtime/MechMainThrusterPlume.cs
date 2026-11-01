using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Renders physical main-engine plumes and smaller maneuvering plumes at confirmed imported
    /// nozzle anchors. Exhaust direction is opposite the force applied to the vehicle.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMainThrusterPlume : MonoBehaviour
    {
        public const string NozzleLayoutResourcePath = "Mech/ThrusterNozzleLayout";

        private static readonly int CoreColorId = Shader.PropertyToID("_CoreColor");
        private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int SoftnessId = Shader.PropertyToID("_Softness");
        private static readonly int FlowSpeedId = Shader.PropertyToID("_FlowSpeed");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");

        [SerializeField] private MechFlightMotionController motion;
        [SerializeField, Min(0f)] private float minimumVisibleForceKn = 2f;
        [SerializeField, Min(1f)] private float referenceForceKn = 650f;
        [SerializeField, Min(0.05f)] private float nozzleRadius = 0.42f;
        [SerializeField, Min(0.1f)] private float minimumLength = 1.4f;
        [SerializeField, Min(0.2f)] private float ratedLength = 8.5f;
        [SerializeField, Min(0.1f)] private float responseSpeed = 9f;
        [SerializeField] private Color coreColor = new(1.55f, 2.8f, 5.8f, 1f);
        [SerializeField] private Color edgeColor = new(0.08f, 0.75f, 3.8f, 1f);

        [Header("Auxiliary Nozzles")]
        [SerializeField] private bool auxiliaryPlumesEnabled = true;
        [SerializeField, Range(0f, 0.95f)] private float auxiliaryAlignmentThreshold = 0.08f;
        [SerializeField, Min(0.01f)] private float auxiliaryNozzleRadius = 0.13f;
        [SerializeField, Min(0.05f)] private float auxiliaryMinimumLength = 0.22f;
        [SerializeField, Min(0.1f)] private float auxiliaryRatedLength = 1.45f;
        [SerializeField, Min(0.1f)] private float auxiliaryResponseSpeed = 15f;

        private readonly Dictionary<string, PlumeEmitter> emitters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlumeEmitter> auxiliaryEmitters =
            new(StringComparer.Ordinal);
        private readonly List<string> staleIds = new();
        private Mesh plumeMesh;
        private Material plumeMaterial;
        private int reportedEmitterCount = -1;
        private int assemblySignature = int.MinValue;
        private int reportedAuxiliaryEmitterCount = -1;
        private MechRuntimeState runtimeState;
        private MechThrusterNozzleLayoutData nozzleLayout = new();
        private bool nozzleLayoutLoaded;
        private string editorPreviewNozzleId;
        private string editorPreviewMirrorNozzleId;
        private bool hasReportedAuxiliaryRuntimeState;
        private bool lastReportedAuxiliaryInputActive;
        private int lastReportedVisibleAuxiliaryCount = -1;

        public int ActivePhysicalEmitterCount => emitters.Count;
        public int ActiveLogicalThrusterCount { get; private set; }
        public int DiscoveredAuxiliaryNozzleCount { get; private set; }
        public int AppliedVisualOverrideCount { get; private set; }
        public int UnmatchedVisualOverrideCount { get; private set; }

        internal readonly struct AuxiliaryNozzleBinding
        {
            public AuxiliaryNozzleBinding(
                string id,
                Transform anchor,
                Transform directionReference,
                Vector3 baseExhaustDirectionLocal,
                MechPartSlot slot,
                string nodeName,
                float ratedThrustKn,
                bool vectoringEnabled,
                float maximumVectorAngleDegrees)
            {
                Id = id;
                Anchor = anchor;
                DirectionReference = directionReference;
                BaseExhaustDirectionLocal = baseExhaustDirectionLocal;
                Slot = slot;
                NodeName = nodeName;
                RatedThrustKn = ratedThrustKn;
                VectoringEnabled = vectoringEnabled;
                MaximumVectorAngleDegrees = maximumVectorAngleDegrees;
            }

            public string Id { get; }
            public Transform Anchor { get; }
            public Transform DirectionReference { get; }
            public Vector3 BaseExhaustDirectionLocal { get; }
            public MechPartSlot Slot { get; }
            public string NodeName { get; }
            public float RatedThrustKn { get; }
            public bool VectoringEnabled { get; }
            public float MaximumVectorAngleDegrees { get; }
        }

        internal void CollectAuxiliaryNozzleBindings(
            List<AuxiliaryNozzleBinding> bindings)
        {
            bindings.Clear();
            foreach (var pair in auxiliaryEmitters)
            {
                var emitter = pair.Value;
                if (emitter.Root == null || emitter.AuxiliaryAnchor == null)
                {
                    continue;
                }

                var baseExhaustDirection = ResolveAuxiliaryVisualDirection(
                    emitter,
                    Vector3.zero);
                bindings.Add(new AuxiliaryNozzleBinding(
                    emitter.LayoutId,
                    emitter.Root,
                    emitter.AuxiliaryAnchor,
                    emitter.AuxiliaryAnchor.InverseTransformDirection(
                        baseExhaustDirection).normalized,
                    emitter.AuxiliarySlot,
                    emitter.AnchorName,
                    emitter.RatedThrustKn,
                    emitter.VectoringEnabled,
                    emitter.MaximumVectorAngleDegrees));
            }
        }

        internal bool CanAuxiliaryNozzleProvideForce(
            Transform nozzleRoot,
            Vector3 requestedForce)
        {
            if (nozzleRoot == null || requestedForce.sqrMagnitude <= Mathf.Epsilon)
            {
                return false;
            }

            var desiredExhaustDirection = -requestedForce.normalized;
            return Vector3.Dot(nozzleRoot.forward, desiredExhaustDirection) >
                   auxiliaryAlignmentThreshold;
        }

        internal bool TryResolveAuxiliaryNozzleAllocation(
            Transform nozzleRoot,
            Vector3 requestedForce,
            out Vector3 forceDirection,
            out float ratedThrustKn)
        {
            foreach (var pair in auxiliaryEmitters)
            {
                var emitter = pair.Value;
                if (emitter.Root != nozzleRoot || emitter.AuxiliaryAnchor == null)
                {
                    continue;
                }

                var baseExhaustDirection = ResolveAuxiliaryVisualDirection(
                    emitter,
                    Vector3.zero);
                forceDirection = -baseExhaustDirection.normalized;
                if (emitter.VectoringEnabled &&
                    emitter.MaximumVectorAngleDegrees > 0f &&
                    requestedForce.sqrMagnitude > Mathf.Epsilon)
                {
                    forceDirection = Vector3.RotateTowards(
                        forceDirection,
                        requestedForce.normalized,
                        emitter.MaximumVectorAngleDegrees * Mathf.Deg2Rad,
                        0f).normalized;
                }

                ratedThrustKn = Mathf.Max(0f, emitter.RatedThrustKn);
                return true;
            }

            forceDirection = Vector3.zero;
            ratedThrustKn = 0f;
            return false;
        }

        public void Configure(MechFlightMotionController controller)
        {
            motion = controller;
            // This component is the runtime owner of the assembled mech's maneuvering plumes.
            // Editor preview state must never leave them disabled when entering play mode.
            auxiliaryPlumesEnabled = true;
            if (!Application.isPlaying || motion == null)
            {
                return;
            }

            motion.RefreshThrusterLayout();
            EnsureResources();
            SynchronizeEmitters();
            SynchronizeAuxiliaryEmitters(forceRebuild: true);
        }

        private void Awake()
        {
            motion ??= GetComponent<MechFlightMotionController>();
            runtimeState ??= GetComponent<MechRuntimeState>();
            LoadNozzleLayout();
            EnsureResources();
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                auxiliaryPlumesEnabled = true;
            }
        }

        private void LateUpdate()
        {
            using var costScope=ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Plume);
            if (motion == null)
            {
                return;
            }

            EnsureResources();
            SynchronizeEmitters();
            SynchronizeAuxiliaryEmitters(forceRebuild: false);
            foreach (var pair in emitters)
            {
                UpdateEmitter(pair.Value);
            }

            ActiveLogicalThrusterCount = 0;
            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                if (channels[i].CurrentForceNewtons.sqrMagnitude > 0.0001f)
                {
                    ActiveLogicalThrusterCount++;
                }
            }

            var anyThrusterActive = ActiveLogicalThrusterCount > 0;
            var visibleAuxiliaryCount = 0;
            foreach (var pair in auxiliaryEmitters)
            {
                UpdateAuxiliaryEmitter(pair.Value);
                if (pair.Value.Visible)
                {
                    visibleAuxiliaryCount++;
                }
            }

            ReportAuxiliaryRuntimeState(
                anyThrusterActive,
                visibleAuxiliaryCount);
        }

        [SerializeField] private bool logAuxiliaryRuntimeDiagnostics;
        private float nextAuxiliaryRuntimeDiagnostic;

        private void ReportAuxiliaryRuntimeState(
            bool inputActive,
            int visibleCount)
        {
            if (!logAuxiliaryRuntimeDiagnostics || Time.unscaledTime < nextAuxiliaryRuntimeDiagnostic) return;
            nextAuxiliaryRuntimeDiagnostic = Time.unscaledTime + 1f;
            if (hasReportedAuxiliaryRuntimeState &&
                inputActive == lastReportedAuxiliaryInputActive &&
                visibleCount == lastReportedVisibleAuxiliaryCount)
            {
                return;
            }

            hasReportedAuxiliaryRuntimeState = true;
            lastReportedAuxiliaryInputActive = inputActive;
            lastReportedVisibleAuxiliaryCount = visibleCount;

            var sampleId = "none";
            var sampleIntensity = 0f;
            var samplePosition = Vector3.zero;
            foreach (var pair in auxiliaryEmitters)
            {
                sampleId = pair.Key;
                sampleIntensity = pair.Value.Intensity;
                samplePosition = pair.Value.Root != null
                    ? pair.Value.Root.position
                    : Vector3.zero;
                break;
            }

            Debug.Log(
                $"[MechMainThrusterPlume] Auxiliary runtime: input={inputActive}, " +
                $"translationInput={motion.TranslationInputActive}, " +
                $"activeChannels={ActiveLogicalThrusterCount}, " +
                $"bound={auxiliaryEmitters.Count}, visible={visibleCount}, " +
                $"sample={sampleId}, intensity={sampleIntensity:F2}, " +
                $"position={samplePosition}.",
                this);
        }

        private void SynchronizeAuxiliaryEmitters(bool forceRebuild)
        {
            runtimeState ??= GetComponent<MechRuntimeState>();
            LoadNozzleLayout();
            var currentSignature = CalculateAssemblySignature();
            if (!forceRebuild &&
                currentSignature == assemblySignature &&
                AuxiliaryEmitterAnchorsAreValid())
            {
                return;
            }

            EnsureMirroredRightForearmNozzle();
            assemblySignature = currentSignature;
            foreach (var pair in auxiliaryEmitters)
            {
                if (pair.Value.Root != null)
                {
                    Destroy(pair.Value.Root.gameObject);
                }
            }
            auxiliaryEmitters.Clear();

            var headCount = 0;
            var coreCount = 0;
            var armCount = 0;
            var legCount = 0;
            var boosterCount = 0;
            var configuredBySource = BuildConfiguredNozzlesBySource();
            var discoveredSources = new HashSet<string>(StringComparer.Ordinal);
            DiscoveredAuxiliaryNozzleCount = 0;
            AppliedVisualOverrideCount = 0;
            var candidates = GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (!TryResolveAuxiliaryNozzleSlot(candidate, out var slot) ||
                    !IsAuxiliaryNozzleAnchor(slot, candidate.name))
                {
                    continue;
                }

                var sourceId = BuildNozzleSourceId(slot, candidate);
                discoveredSources.Add(sourceId);
                DiscoveredAuxiliaryNozzleCount++;
                if (configuredBySource.TryGetValue(sourceId, out var configuredNozzles))
                {
                    MechThrusterNozzleLayoutEntry primaryOverride = null;
                    var primaryConfigured = false;
                    for (var entryIndex = 0; entryIndex < configuredNozzles.Count; entryIndex++)
                    {
                        var entry = configuredNozzles[entryIndex];
                        if (string.IsNullOrWhiteSpace(entry.id))
                        {
                            continue;
                        }

                        if (string.Equals(entry.id, sourceId, StringComparison.Ordinal))
                        {
                            primaryConfigured = true;
                            if (entry.enabled)
                            {
                                primaryOverride = entry;
                            }
                            continue;
                        }

                        if (!entry.enabled)
                        {
                            continue;
                        }

                        // Editor-created duplicates remain separate physical nozzle origins.
                        AddAuxiliaryEmitter(candidate, slot, sourceId, entry);
                        AppliedVisualOverrideCount++;
                        IncrementAuxiliaryCount(
                            slot,
                            ref headCount,
                            ref coreCount,
                            ref armCount,
                            ref legCount,
                            ref boosterCount);
                    }

                    if (primaryOverride != null)
                    {
                        AddAuxiliaryEmitter(candidate, slot, sourceId, primaryOverride);
                        AppliedVisualOverrideCount++;
                    }
                    else if (!primaryConfigured)
                    {
                        AddAuxiliaryEmitter(candidate, slot, sourceId, null);
                    }
                }
                else
                {
                    // Preserve the verified legacy path: every recognized model dummy produces
                    // its normal runtime plume even when no visual override package exists.
                    AddAuxiliaryEmitter(candidate, slot, sourceId, null);
                }

                IncrementAuxiliaryCount(
                    slot,
                    ref headCount,
                    ref coreCount,
                    ref armCount,
                    ref legCount,
                    ref boosterCount);
            }

            legCount += SynchronizeMirroredLegVisuals();

            UnmatchedVisualOverrideCount = CountUnmatchedVisualOverrides(discoveredSources);

            if (reportedAuxiliaryEmitterCount != auxiliaryEmitters.Count)
            {
                reportedAuxiliaryEmitterCount = auxiliaryEmitters.Count;
                Debug.Log(
                    $"[MechMainThrusterPlume] Bound auxiliary emitters={auxiliaryEmitters.Count} " +
                    $"(head={headCount}, core={coreCount}, arms={armCount}, " +
                    $"legs={legCount}, booster={boosterCount}).",
                    this);
            }

            if (UnmatchedVisualOverrideCount > 0)
            {
                Debug.LogWarning(
                    $"[MechMainThrusterPlume] Ignored {UnmatchedVisualOverrideCount} unmatched " +
                    "visual override entries. Legacy nozzle discovery remains active.",
                    this);
            }

            foreach (var pair in auxiliaryEmitters)
            {
                SetAuxiliaryEmitterPose(pair.Value);
            }

            ApplyLeftAuthoritativeNozzleMirroring();

            motion?.RefreshThrusterLayout();
        }

        private void ApplyLeftAuthoritativeNozzleMirroring()
        {
            var leftNozzleIds = new List<string>();
            foreach (var pair in auxiliaryEmitters)
            {
                var emitter = pair.Value;
                if (emitter.Root == null ||
                    emitter.AuxiliaryAnchor == null ||
                    !TryGetMirroredNozzleId(emitter.LayoutId, out _))
                {
                    continue;
                }

                var localPosition = transform.InverseTransformPoint(emitter.Root.position);
                if (localPosition.x < -0.01f)
                {
                    leftNozzleIds.Add(emitter.LayoutId);
                }
            }

            for (var i = 0; i < leftNozzleIds.Count; i++)
            {
                if (!auxiliaryEmitters.TryGetValue(leftNozzleIds[i], out var source) ||
                    source.Root == null)
                {
                    continue;
                }

                UpdateNozzleEditorState(
                    source.LayoutId,
                    source.Root.position,
                    source.Root.rotation,
                    source.RadiusScale,
                    source.LengthScale,
                    source.RatedThrustKn,
                    source.VectoringEnabled,
                    source.MaximumVectorAngleDegrees,
                    updateMirror: true);
            }
        }

        private bool AuxiliaryEmitterAnchorsAreValid()
        {
            // An assembly with no auxiliary nozzles is a valid cached result.
            foreach (var pair in auxiliaryEmitters)
            {
                if (pair.Value.AuxiliaryAnchor == null || pair.Value.Root == null)
                {
                    return false;
                }
            }

            return true;
        }

        private int SynchronizeMirroredLegVisuals()
        {
            var snapshot = new List<PlumeEmitter>(auxiliaryEmitters.Values);
            var addedCount = 0;
            for (var i = 0; i < snapshot.Count; i++)
            {
                var source = snapshot[i];
                if (source.AuxiliarySlot != MechPartSlot.Legs ||
                    source.AuxiliaryAnchor == null ||
                    string.IsNullOrWhiteSpace(source.SourceId) ||
                    source.SourceId.IndexOf("/L_Coxa", StringComparison.OrdinalIgnoreCase) < 0 ||
                    !TryGetMirroredAnchorName(
                        source.AuxiliaryAnchor.name,
                        out var mirroredAnchorName))
                {
                    continue;
                }

                var sourceOrdinal = FindLayoutSourceOrdinal(source.SourceId, source.LayoutId);
                if (sourceOrdinal < 0)
                {
                    continue;
                }

                PlumeEmitter mirroredBase = null;
                for (var candidateIndex = 0; candidateIndex < snapshot.Count; candidateIndex++)
                {
                    var candidate = snapshot[candidateIndex];
                    if (candidate.AuxiliarySlot == MechPartSlot.Legs &&
                        candidate.AuxiliaryAnchor != null &&
                        candidate.SourceId.IndexOf(
                            "/R_Coxa",
                            StringComparison.OrdinalIgnoreCase) >= 0 &&
                        candidate.AnchorName.Equals(
                            mirroredAnchorName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        mirroredBase = candidate;
                        break;
                    }
                }

                if (mirroredBase == null)
                {
                    continue;
                }

                PlumeEmitter target = null;
                for (var candidateIndex = 0; candidateIndex < snapshot.Count; candidateIndex++)
                {
                    var candidate = snapshot[candidateIndex];
                    if (string.Equals(
                            candidate.SourceId,
                            mirroredBase.SourceId,
                            StringComparison.Ordinal) &&
                        FindLayoutSourceOrdinal(
                            candidate.SourceId,
                            candidate.LayoutId) == sourceOrdinal)
                    {
                        target = candidate;
                        break;
                    }
                }

                if (target == null)
                {
                    var mirroredEntry = new MechThrusterNozzleLayoutEntry
                    {
                        id = $"{source.LayoutId}_mirrored",
                        sourceId = mirroredBase.SourceId,
                        displayName = $"{source.DisplayName} / 右腿镜像",
                        enabled = true,
                        radiusScale = source.RadiusScale,
                        lengthScale = source.LengthScale,
                        hasPhysicalSettings = true,
                        ratedThrustKn = source.RatedThrustKn,
                        vectoringEnabled = source.VectoringEnabled,
                        maximumVectorAngleDegrees = source.MaximumVectorAngleDegrees,
                    };
                    AddAuxiliaryEmitter(
                        mirroredBase.AuxiliaryAnchor,
                        MechPartSlot.Legs,
                        mirroredBase.SourceId,
                        mirroredEntry);
                    if (!auxiliaryEmitters.TryGetValue(mirroredEntry.id, out target))
                    {
                        continue;
                    }

                    nozzleLayout.nozzles.Add(mirroredEntry);
                    snapshot.Add(target);
                    addedCount++;
                }

                ApplyMirroredVisualState(source, target);
            }

            return addedCount;
        }

        private void ApplyMirroredVisualState(
            PlumeEmitter source,
            PlumeEmitter target)
        {
            SetAuxiliaryEmitterPose(source);
            var localPosition = transform.InverseTransformPoint(source.Root.position);
            localPosition.x = -localPosition.x;

            var sourceForward = transform.InverseTransformDirection(source.Root.forward);
            var sourceUp = transform.InverseTransformDirection(source.Root.up);
            sourceForward.x = -sourceForward.x;
            sourceUp.x = -sourceUp.x;
            var mirroredRotation = Quaternion.LookRotation(
                transform.TransformDirection(sourceForward),
                transform.TransformDirection(sourceUp));

            target.UsesManualTransform = true;
            target.LocalPositionOffset = target.AuxiliaryAnchor.InverseTransformPoint(
                transform.TransformPoint(localPosition));
            target.LocalEulerOffset = (Quaternion.Inverse(target.AuxiliaryAnchor.rotation) *
                                       mirroredRotation).eulerAngles;
            target.RadiusScale = source.RadiusScale;
            target.LengthScale = source.LengthScale;
            target.RatedThrustKn = source.RatedThrustKn;
            target.VectoringEnabled = source.VectoringEnabled;
            target.MaximumVectorAngleDegrees = source.MaximumVectorAngleDegrees;

            var targetEntry = FindLayoutEntry(target.LayoutId);
            if (targetEntry != null)
            {
                targetEntry.localPositionOffset = target.LocalPositionOffset;
                targetEntry.localEulerOffset = target.LocalEulerOffset;
                targetEntry.radiusScale = target.RadiusScale;
                targetEntry.lengthScale = target.LengthScale;
                targetEntry.hasPhysicalSettings = true;
                targetEntry.ratedThrustKn = target.RatedThrustKn;
                targetEntry.vectoringEnabled = target.VectoringEnabled;
                targetEntry.maximumVectorAngleDegrees = target.MaximumVectorAngleDegrees;
            }

            SetAuxiliaryEmitterPose(target);
        }

        private int CountUnmatchedVisualOverrides(HashSet<string> discoveredSources)
        {
            if (nozzleLayout?.nozzles == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < nozzleLayout.nozzles.Count; i++)
            {
                var entry = nozzleLayout.nozzles[i];
                if (entry != null &&
                    !string.IsNullOrWhiteSpace(entry.sourceId) &&
                    !discoveredSources.Contains(entry.sourceId))
                {
                    count++;
                }
            }

            return count;
        }

        private void AddAuxiliaryEmitter(
            Transform candidate,
            MechPartSlot slot,
            string sourceId,
            MechThrusterNozzleLayoutEntry entry)
        {
            var layoutId = entry != null ? entry.id : sourceId;
            if (auxiliaryEmitters.ContainsKey(layoutId))
            {
                return;
            }

            var emitter = CreateEmitter(layoutId, "Auxiliary_Thruster_Plume");
            emitter.AuxiliaryAnchor = candidate;
            emitter.AnchorName = candidate.name;
            emitter.MirrorAnchorName = TryGetMirroredAnchorName(emitter.AnchorName,out var mirrorName)?mirrorName:null;
            emitter.AuxiliarySlot = slot;
            emitter.LayoutId = layoutId;
            emitter.SourceId = sourceId;
            emitter.DisplayName = entry != null && !string.IsNullOrWhiteSpace(entry.displayName)
                ? entry.displayName
                : $"{slot} / {candidate.name}";
            emitter.RatedThrustKn = 900f;
            emitter.ShoulderDirection = ResolveShoulderNozzleDirection(candidate, slot);
            if (entry != null)
            {
                entry.NormalizePhysicalSettings();
                emitter.UsesManualTransform = true;
                emitter.LocalPositionOffset = entry.localPositionOffset;
                emitter.LocalEulerOffset = entry.localEulerOffset;
                emitter.RadiusScale = Mathf.Max(0.05f, entry.radiusScale);
                emitter.LengthScale = Mathf.Max(0.05f, entry.lengthScale);
                emitter.RatedThrustKn = entry.ratedThrustKn;
                emitter.VectoringEnabled = entry.vectoringEnabled;
                emitter.MaximumVectorAngleDegrees = entry.maximumVectorAngleDegrees;
            }

            auxiliaryEmitters.Add(layoutId, emitter);
        }

        private static void IncrementAuxiliaryCount(
            MechPartSlot slot,
            ref int headCount,
            ref int coreCount,
            ref int armCount,
            ref int legCount,
            ref int boosterCount)
        {
            switch (slot)
            {
                case MechPartSlot.Head:
                    headCount++;
                    break;
                case MechPartSlot.Core:
                    coreCount++;
                    break;
                case MechPartSlot.Arms:
                case MechPartSlot.LeftArm:
                case MechPartSlot.RightArm:
                    armCount++;
                    break;
                case MechPartSlot.Legs:
                    legCount++;
                    break;
                case MechPartSlot.Booster:
                    boosterCount++;
                    break;
            }
        }

        private void EnsureMirroredRightForearmNozzle()
        {
            const string sourceName = "AM_M_1010_dmy_25";
            const string mirroredName = "AM_M_1010_dmy_25_MIRRORED";
            var parts = GetComponentsInChildren<MechAssembledPart>(true);
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

                    if (candidate.name.Equals(
                            sourceName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        source = candidate;
                    }
                    else if (candidate.name.Equals(
                                 "R_ForeArm",
                                 StringComparison.OrdinalIgnoreCase))
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
                mirrored.localScale = Vector3.one;
            }
        }

        private int CalculateAssemblySignature()
        {
            if (runtimeState == null)
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                var parts = runtimeState.AssembledParts;
                hash = hash * 31 + parts.Count;
                for (var i = 0; i < parts.Count; i++)
                {
                    var record = parts[i];
                    hash = hash * 31 + (int)(record?.slot ?? MechPartSlot.None);
                    hash = hash * 31 + (record?.instance != null
                        ? record.instance.GetEntityId().GetHashCode()
                        : 0);
                }
                return hash;
            }
        }

        public void ReloadNozzleLayout()
        {
            nozzleLayoutLoaded = false;
            LoadNozzleLayout();
            assemblySignature = int.MinValue;
            if (Application.isPlaying)
            {
                SynchronizeAuxiliaryEmitters(forceRebuild: true);
            }
        }

        public void ApplyNozzleLayout(MechThrusterNozzleLayoutData layout)
        {
            nozzleLayout = layout?.Clone() ?? new MechThrusterNozzleLayoutData();
            nozzleLayout.NormalizePackageMetadata();
            nozzleLayoutLoaded = true;
            assemblySignature = int.MinValue;
            if (Application.isPlaying)
            {
                SynchronizeAuxiliaryEmitters(forceRebuild: true);
            }
        }

        public MechThrusterNozzleLayoutData CaptureCurrentNozzleLayout()
        {
            var captured = new MechThrusterNozzleLayoutData();
            foreach (var pair in auxiliaryEmitters)
            {
                var emitter = pair.Value;
                if (emitter.AuxiliaryAnchor == null)
                {
                    continue;
                }

                var direction = ResolveAuxiliaryVisualDirection(emitter, Vector3.zero);
                var up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.95f
                    ? transform.right
                    : Vector3.up;
                var worldRotation = Quaternion.LookRotation(direction, up);
                captured.nozzles.Add(new MechThrusterNozzleLayoutEntry
                {
                    id = emitter.LayoutId,
                    sourceId = emitter.SourceId,
                    displayName = emitter.DisplayName,
                    enabled = true,
                    localPositionOffset = emitter.LocalPositionOffset,
                    localEulerOffset = (Quaternion.Inverse(emitter.AuxiliaryAnchor.rotation) *
                                        worldRotation).eulerAngles,
                    radiusScale = emitter.RadiusScale,
                    lengthScale = emitter.LengthScale,
                    hasPhysicalSettings = true,
                    ratedThrustKn = emitter.RatedThrustKn,
                    vectoringEnabled = emitter.VectoringEnabled,
                    maximumVectorAngleDegrees = emitter.MaximumVectorAngleDegrees,
                });
            }

            captured.NormalizePackageMetadata();
            return captured;
        }

        public MechThrusterNozzleLayoutData ExportNozzleLayout()
        {
            return nozzleLayout?.Clone() ?? new MechThrusterNozzleLayoutData();
        }

        public void GetNozzleEditorStates(List<MechThrusterNozzleEditorState> target)
        {
            if (target == null)
            {
                return;
            }

            target.Clear();
            foreach (var pair in auxiliaryEmitters)
            {
                var emitter = pair.Value;
                if (emitter.AuxiliaryAnchor == null)
                {
                    continue;
                }

                var direction = ResolveAuxiliaryVisualDirection(emitter, Vector3.zero);
                var up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.95f
                    ? transform.right
                    : Vector3.up;
                target.Add(new MechThrusterNozzleEditorState(
                    emitter.LayoutId,
                    emitter.SourceId,
                    emitter.DisplayName,
                    emitter.AuxiliaryAnchor.TransformPoint(emitter.LocalPositionOffset),
                    Quaternion.LookRotation(direction, up),
                    emitter.RadiusScale,
                    emitter.LengthScale,
                    emitter.RatedThrustKn,
                    emitter.VectoringEnabled,
                    emitter.MaximumVectorAngleDegrees));
            }
        }

        public void SetNozzleEditorPreview(string id, bool includeMirror = false)
        {
            editorPreviewNozzleId = id;
            editorPreviewMirrorNozzleId = includeMirror &&
                                          TryGetMirroredNozzleId(id, out var mirroredId)
                ? mirroredId
                : null;
        }

        public bool TryGetMirroredNozzleId(string id, out string mirroredId)
        {
            mirroredId = null;
            if (string.IsNullOrWhiteSpace(id) ||
                !auxiliaryEmitters.TryGetValue(id, out var emitter) ||
                emitter.AuxiliaryAnchor == null ||
                string.IsNullOrEmpty(emitter.MirrorAnchorName))
            {
                return false;
            }

            var mirroredAnchorName=emitter.MirrorAnchorName;
            var sourceSide = Mathf.Sign(Vector3.Dot(
                emitter.AuxiliaryAnchor.position - transform.position,
                transform.right));
            string mirroredSourceId = null;
            foreach (var pair in auxiliaryEmitters)
            {
                var candidate = pair.Value;
                if (candidate.AuxiliaryAnchor == null ||
                    !candidate.AnchorName.Equals(
                        mirroredAnchorName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateSide = Mathf.Sign(Vector3.Dot(
                    candidate.AuxiliaryAnchor.position - transform.position,
                    transform.right));
                if (sourceSide == 0f || candidateSide == 0f || sourceSide != candidateSide)
                {
                    mirroredSourceId = candidate.SourceId;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(mirroredSourceId))
            {
                return false;
            }

            var sourceOrdinal = FindLayoutSourceOrdinal(emitter.SourceId, id);
            if (sourceOrdinal < 0)
            {
                foreach (var pair in auxiliaryEmitters)
                {
                    if (string.Equals(
                            pair.Value.SourceId,
                            mirroredSourceId,
                            StringComparison.Ordinal))
                    {
                        mirroredId = pair.Value.LayoutId;
                        return true;
                    }
                }

                return false;
            }

            var mirroredOrdinal = 0;
            for (var i = 0; i < nozzleLayout.nozzles.Count; i++)
            {
                var entry = nozzleLayout.nozzles[i];
                if (entry == null ||
                    !string.Equals(entry.sourceId, mirroredSourceId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (mirroredOrdinal == sourceOrdinal)
                {
                    mirroredId = entry.id;
                    return true;
                }

                mirroredOrdinal++;
            }

            return false;
        }

        public bool UpdateNozzleEditorState(
            string id,
            Vector3 worldPosition,
            Quaternion worldRotation,
            float radiusScale,
            float lengthScale,
            float ratedThrustKn,
            bool vectoringEnabled,
            float maximumVectorAngleDegrees,
            bool updateMirror = false)
        {
            if (string.IsNullOrWhiteSpace(id) ||
                !auxiliaryEmitters.TryGetValue(id, out var emitter) ||
                emitter.AuxiliaryAnchor == null)
            {
                return false;
            }

            var entry = FindLayoutEntry(id);
            if (entry == null)
            {
                return false;
            }

            entry.localPositionOffset = emitter.AuxiliaryAnchor.InverseTransformPoint(worldPosition);
            entry.localEulerOffset = (Quaternion.Inverse(emitter.AuxiliaryAnchor.rotation) *
                                      worldRotation).eulerAngles;
            entry.radiusScale = Mathf.Max(0.05f, radiusScale);
            entry.lengthScale = Mathf.Max(0.05f, lengthScale);
            entry.hasPhysicalSettings = true;
            entry.ratedThrustKn = Mathf.Max(0f, ratedThrustKn);
            entry.vectoringEnabled = vectoringEnabled;
            entry.maximumVectorAngleDegrees = vectoringEnabled
                ? Mathf.Clamp(maximumVectorAngleDegrees, 0f, 89f)
                : 0f;

            emitter.UsesManualTransform = true;
            emitter.LocalPositionOffset = entry.localPositionOffset;
            emitter.LocalEulerOffset = entry.localEulerOffset;
            emitter.RadiusScale = entry.radiusScale;
            emitter.LengthScale = entry.lengthScale;
            emitter.RatedThrustKn = entry.ratedThrustKn;
            emitter.VectoringEnabled = entry.vectoringEnabled;
            emitter.MaximumVectorAngleDegrees = entry.maximumVectorAngleDegrees;
            SetAuxiliaryEmitterPose(emitter);

            if (updateMirror && TryGetMirroredNozzleId(id, out var mirroredId))
            {
                var localPosition = transform.InverseTransformPoint(worldPosition);
                localPosition.x = -localPosition.x;

                var localForward = transform.InverseTransformDirection(
                    worldRotation * Vector3.forward);
                var localUp = transform.InverseTransformDirection(
                    worldRotation * Vector3.up);
                localForward.x = -localForward.x;
                localUp.x = -localUp.x;
                var mirroredRotation = Quaternion.LookRotation(
                    transform.TransformDirection(localForward),
                    transform.TransformDirection(localUp));

                UpdateNozzleEditorState(
                    mirroredId,
                    transform.TransformPoint(localPosition),
                    mirroredRotation,
                    radiusScale,
                    lengthScale,
                    ratedThrustKn,
                    vectoringEnabled,
                    maximumVectorAngleDegrees,
                    updateMirror: false);
            }

            return true;
        }

        public bool UpdateNozzlePhysicalSettings(
            string id,
            float ratedThrustKn,
            bool vectoringEnabled,
            float maximumVectorAngleDegrees,
            bool updateMirror = false)
        {
            if (!ApplyNozzlePhysicalSettings(
                    id,
                    ratedThrustKn,
                    vectoringEnabled,
                    maximumVectorAngleDegrees))
            {
                return false;
            }

            if (updateMirror && TryGetMirroredNozzleId(id, out var mirroredId))
            {
                ApplyNozzlePhysicalSettings(
                    mirroredId,
                    ratedThrustKn,
                    vectoringEnabled,
                    maximumVectorAngleDegrees);
            }

            return true;
        }

        private bool ApplyNozzlePhysicalSettings(
            string id,
            float ratedThrustKn,
            bool vectoringEnabled,
            float maximumVectorAngleDegrees)
        {
            if (string.IsNullOrWhiteSpace(id) ||
                !auxiliaryEmitters.TryGetValue(id, out var emitter))
            {
                return false;
            }

            var entry = FindLayoutEntry(id);
            if (entry == null)
            {
                return false;
            }

            entry.hasPhysicalSettings = true;
            entry.ratedThrustKn = Mathf.Max(0f, ratedThrustKn);
            entry.vectoringEnabled = vectoringEnabled;
            entry.maximumVectorAngleDegrees = vectoringEnabled
                ? Mathf.Clamp(maximumVectorAngleDegrees, 0f, 89f)
                : 0f;

            emitter.RatedThrustKn = entry.ratedThrustKn;
            emitter.VectoringEnabled = entry.vectoringEnabled;
            emitter.MaximumVectorAngleDegrees = entry.maximumVectorAngleDegrees;
            return true;
        }

        private int FindLayoutSourceOrdinal(string sourceId, string id)
        {
            if (nozzleLayout?.nozzles == null)
            {
                return -1;
            }

            var ordinal = 0;
            for (var i = 0; i < nozzleLayout.nozzles.Count; i++)
            {
                var entry = nozzleLayout.nozzles[i];
                if (entry == null ||
                    !string.Equals(entry.sourceId, sourceId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(entry.id, id, StringComparison.Ordinal))
                {
                    return ordinal;
                }

                ordinal++;
            }

            return -1;
        }

        private static bool TryGetMirroredAnchorName(string sourceName, out string mirroredName)
        {
            mirroredName = null;
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return false;
            }

            if (sourceName.EndsWith("_dmy_70", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^1] + "1";
            }
            else if (sourceName.EndsWith("_dmy_71", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^1] + "0";
            }
            else if (sourceName.EndsWith("_dmy_7", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^1] + "8";
            }
            else if (sourceName.EndsWith("_dmy_8", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^1] + "7";
            }
            else if (sourceName.EndsWith("_dmy_25_MIRRORED", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^"_MIRRORED".Length];
            }
            else if (sourceName.EndsWith("_dmy_25", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName + "_MIRRORED";
            }
            else if (sourceName.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^2] + "33";
            }
            else if (sourceName.EndsWith("_dmy_33", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^2] + "31";
            }
            else if (sourceName.EndsWith("_dmy_32", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^2] + "34";
            }
            else if (sourceName.EndsWith("_dmy_34", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^2] + "32";
            }
            else if (sourceName.EndsWith("_dmy_830", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^3] + "831";
            }
            else if (sourceName.EndsWith("_dmy_831", StringComparison.OrdinalIgnoreCase))
            {
                mirroredName = sourceName[..^3] + "830";
            }

            return !string.IsNullOrWhiteSpace(mirroredName);
        }

        private MechThrusterNozzleLayoutEntry FindLayoutEntry(string id)
        {
            if (nozzleLayout?.nozzles == null)
            {
                return null;
            }

            for (var i = 0; i < nozzleLayout.nozzles.Count; i++)
            {
                var entry = nozzleLayout.nozzles[i];
                if (entry != null && string.Equals(entry.id, id, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        private void LoadNozzleLayout()
        {
            if (nozzleLayoutLoaded)
            {
                return;
            }

            nozzleLayoutLoaded = true;
            var asset = Resources.Load<TextAsset>(NozzleLayoutResourcePath);
            nozzleLayout = asset != null
                ? JsonUtility.FromJson<MechThrusterNozzleLayoutData>(asset.text)
                : new MechThrusterNozzleLayoutData();
            nozzleLayout ??= new MechThrusterNozzleLayoutData();
            nozzleLayout.NormalizePackageMetadata();
        }

        private Dictionary<string, List<MechThrusterNozzleLayoutEntry>>
            BuildConfiguredNozzlesBySource()
        {
            var result = new Dictionary<string, List<MechThrusterNozzleLayoutEntry>>(
                StringComparer.Ordinal);
            if (nozzleLayout?.nozzles == null)
            {
                return result;
            }

            for (var i = 0; i < nozzleLayout.nozzles.Count; i++)
            {
                var entry = nozzleLayout.nozzles[i];
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

        internal static string BuildNozzleSourceId(MechPartSlot slot, Transform candidate)
        {
            var segments = new List<string>();
            var partId = string.Empty;
            for (var current = candidate; current != null; current = current.parent)
            {
                current.TryGetComponent<MechAssembledPart>(out var assembledPart);
                if (assembledPart != null)
                {
                    partId = assembledPart.Definition != null
                        ? assembledPart.Definition.PartId
                        : current.name;
                    break;
                }

                segments.Add($"{current.name}[{current.GetSiblingIndex()}]");
            }

            segments.Reverse();
            return $"{slot}:{partId}:{string.Join("/", segments)}";
        }

        internal static bool TryResolveAuxiliaryNozzleSlot(
            Transform candidate,
            out MechPartSlot slot)
        {
            for (var current = candidate; current != null; current = current.parent)
            {
                current.TryGetComponent<MechAssembledPart>(out var assembledPart);
                if (assembledPart != null)
                {
                    slot = assembledPart.Slot;
                    return true;
                }

                // The second visual instance of a dual-mounted booster is deliberately not an
                // assembly record, but the assembler gives both visual roots this stable prefix.
                if (current.name.StartsWith("Booster_", StringComparison.OrdinalIgnoreCase))
                {
                    slot = MechPartSlot.Booster;
                    return true;
                }
            }

            slot = MechPartSlot.None;
            return false;
        }

        internal static bool IsAuxiliaryNozzleAnchor(MechPartSlot slot, string nodeName)
        {
            if (string.IsNullOrWhiteSpace(nodeName))
            {
                return false;
            }

            if (slot == MechPartSlot.Booster)
            {
                // The booster side dummies sit beside the physical bell, but they are not
                // separate nozzles in the requested visual layout. Only _dmy_31 is used by
                // the main-engine channel and it is handled by SynchronizeEmitters.
                return false;
            }

            if (slot == MechPartSlot.Arms ||
                slot == MechPartSlot.LeftArm ||
                slot == MechPartSlot.RightArm)
            {
                // Shoulder ports are explicitly mirrored in the GLB. The right forearm's
                // authored counterpart is missing, so a deterministic mirror of _dmy_25 is
                // generated under R_ForeArm before this whitelist is evaluated.
                return nodeName.EndsWith("_dmy_7", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_8", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_70", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_71", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_25", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith(
                           "_dmy_25_MIRRORED",
                           StringComparison.OrdinalIgnoreCase);
            }

            if (slot == MechPartSlot.Legs)
            {
                // MELANDER 1010: two side ports per leg, one rear rectangular port per
                // leg, and the single center-pelvis port.
                return nodeName.EndsWith("_dmy_32", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_34", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_201", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_830", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_831", StringComparison.OrdinalIgnoreCase);
            }

            if (slot == MechPartSlot.Head || slot == MechPartSlot.Core)
            {
                return nodeName.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_32", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_33", StringComparison.OrdinalIgnoreCase) ||
                       nodeName.EndsWith("_dmy_34", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private void SynchronizeEmitters()
        {
            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (channel.Role != MechThrusterRole.Main ||
                    !channel.Id.StartsWith("main.", StringComparison.Ordinal) ||
                    channel.Anchor == null ||
                    channel.UsesProvisionalOrigin)
                {
                    continue;
                }

                if (!emitters.TryGetValue(channel.Id, out var emitter))
                {
                    emitter = CreateEmitter(channel.Id);
                    emitters.Add(channel.Id, emitter);
                }

                emitter.Channel = channel;
                BindMainGimbalVisual(emitter, channel.Anchor);
                emitter.SeenThisFrame = true;
            }

            staleIds.Clear();
            foreach (var pair in emitters)
            {
                if (!pair.Value.SeenThisFrame || pair.Value.Channel.Anchor == null)
                {
                    staleIds.Add(pair.Key);
                }
                else
                {
                    pair.Value.SeenThisFrame = false;
                }
            }

            for (var i = 0; i < staleIds.Count; i++)
            {
                var id = staleIds[i];
                if (emitters.TryGetValue(id, out var emitter) && emitter.Root != null)
                {
                    Destroy(emitter.Root.gameObject);
                }

                emitters.Remove(id);
            }

            if (reportedEmitterCount != emitters.Count)
            {
                reportedEmitterCount = emitters.Count;
                Debug.Log(
                    $"[MechMainThrusterPlume] Bound physical main-nozzle emitters={emitters.Count}.",
                    this);
            }
        }

        private void UpdateEmitter(PlumeEmitter emitter)
        {
            var channel = emitter.Channel;
            var forceNewtons = channel.CurrentForceNewtons;
            var forceDirection = forceNewtons.sqrMagnitude > 0.0001f
                ? forceNewtons.normalized
                : motion.CurrentMainVectorForceDirectionWorld.normalized;
            var exhaustDirection = -forceDirection;
            UpdateMainGimbalVisual(emitter, exhaustDirection);

            var forceKn = forceNewtons.magnitude / 1000f;
            var targetIntensity = forceKn < minimumVisibleForceKn
                ? 0f
                : Mathf.Clamp(Mathf.Sqrt(forceKn / Mathf.Max(1f, referenceForceKn)), 0.08f, 1.65f);
            emitter.Intensity = Mathf.MoveTowards(
                emitter.Intensity,
                targetIntensity,
                responseSpeed * Time.deltaTime);

            var visible = emitter.Intensity > 0.002f && channel.Operational;
            emitter.SetVisible(visible);
            if (!visible)
            {
                return;
            }

            var anchor = channel.Anchor;
            // Main-engine force and plume share the same gimballed axis. The imported anchor
            // remains untouched so vectoring cannot corrupt the assembled model hierarchy.
            var up = Mathf.Abs(Vector3.Dot(exhaustDirection, transform.up)) > 0.95f
                ? transform.right
                : transform.up;
            emitter.Root.SetPositionAndRotation(
                channel.ResolveWorldOrigin(transform),
                Quaternion.LookRotation(exhaustDirection, up));

            var rated = Mathf.Clamp01(emitter.Intensity);
            var overdrive = Mathf.Max(0f, emitter.Intensity - 1f);
            var flicker = 0.96f + Mathf.PerlinNoise(emitter.Seed, Time.time * 15f) * 0.08f;
            var length = Mathf.Lerp(minimumLength, ratedLength, rated) *
                         (1f + overdrive * 0.42f) * flicker;
            var radius = nozzleRadius * Mathf.Lerp(0.62f, 1f, rated) *
                         (1f + overdrive * 0.16f);

            SetLayer(
                emitter.Outer,
                emitter.OuterTransform,
                emitter.OuterBlock,
                radius * 1.75f,
                length * 1.08f,
                emitter.Intensity,
                0.26f,
                1.5f,
                18f,
                edgeColor * 0.72f,
                edgeColor,
                emitter.Seed);
            SetLayer(
                emitter.Core,
                emitter.CoreTransform,
                emitter.CoreBlock,
                radius * 0.72f,
                length * 0.76f,
                emitter.Intensity,
                0.72f,
                3.4f,
                24f,
                coreColor,
                edgeColor,
                emitter.Seed + 3.17f);
            SetLayer(
                emitter.NozzleGlow,
                emitter.NozzleGlowTransform,
                emitter.NozzleGlowBlock,
                radius * 1.2f,
                Mathf.Max(0.35f, radius * 1.1f),
                emitter.Intensity,
                0.9f,
                2.1f,
                12f,
                coreColor,
                edgeColor,
                emitter.Seed + 8.31f);
        }

        private void BindMainGimbalVisual(PlumeEmitter emitter, Transform anchor)
        {
            var visualRoot = ResolveMainGimbalVisualRoot(anchor);
            if (visualRoot == null || emitter.MainGimbalVisualRoot == visualRoot)
            {
                return;
            }

            emitter.MainGimbalVisualRoot = visualRoot;
            emitter.MainGimbalParent = visualRoot.parent;
            emitter.MainGimbalRestLocalRotation = visualRoot.localRotation;
            emitter.MainGimbalRestExhaustInParent = emitter.MainGimbalParent != null
                ? emitter.MainGimbalParent.InverseTransformDirection(anchor.forward).normalized
                : anchor.forward.normalized;
        }

        private void UpdateMainGimbalVisual(
            PlumeEmitter emitter,
            Vector3 desiredExhaustDirection)
        {
            var visualRoot = emitter.MainGimbalVisualRoot;
            if (visualRoot == null || desiredExhaustDirection.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            var desiredInParent = emitter.MainGimbalParent != null
                ? emitter.MainGimbalParent.InverseTransformDirection(
                    desiredExhaustDirection.normalized)
                : desiredExhaustDirection.normalized;
            var gimbalDelta = Quaternion.FromToRotation(
                emitter.MainGimbalRestExhaustInParent,
                desiredInParent);
            visualRoot.localRotation = gimbalDelta * emitter.MainGimbalRestLocalRotation;
        }

        private static Transform ResolveMainGimbalVisualRoot(Transform anchor)
        {
            for (var current = anchor; current != null; current = current.parent)
            {
                current.TryGetComponent<MechAssembledPart>(out var assembledPart);
                if ((assembledPart != null && assembledPart.Slot == MechPartSlot.Booster) ||
                    current.name.StartsWith("Booster_", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }

            return null;
        }

        private void UpdateAuxiliaryEmitter(PlumeEmitter emitter)
        {
            var anchor = emitter.AuxiliaryAnchor;
            emitter.LastRequestedForceKn = 0f;
            emitter.LastAlignment = -1f;
            var editorPreview = string.Equals(
                                    editorPreviewNozzleId,
                                    emitter.LayoutId,
                                    StringComparison.Ordinal) ||
                                string.Equals(
                                    editorPreviewMirrorNozzleId,
                                    emitter.LayoutId,
                                    StringComparison.Ordinal);
            var editorSessionActive = !string.IsNullOrWhiteSpace(editorPreviewNozzleId);
            var responseDeltaTime = editorSessionActive
                ? Time.unscaledDeltaTime
                : Time.deltaTime;
            if (!auxiliaryPlumesEnabled ||
                anchor == null ||
                (motion == null && !editorPreview))
            {
                emitter.Intensity = Mathf.MoveTowards(
                    emitter.Intensity,
                    0f,
                    auxiliaryResponseSpeed * responseDeltaTime);
                emitter.SetVisible(emitter.Intensity > 0.002f);
                return;
            }

            var requestedForce = Vector3.zero;
            var outputFraction = 0f;
            if (motion != null)
            {
                var channels = motion.ThrusterChannels;
                for (var i = 0; i < channels.Count; i++)
                {
                    var channel = channels[i];
                    if (!channel.Operational ||
                        channel.Anchor != emitter.Root)
                    {
                        continue;
                    }

                    requestedForce += channel.CurrentForceNewtons;
                    outputFraction = Mathf.Max(
                        outputFraction,
                        channel.CurrentOutputFraction);
                }
            }
            var forceKn = requestedForce.magnitude / 1000f;
            emitter.LastRequestedForceKn = forceKn;
            var targetIntensity = 0f;
            if (outputFraction > 0.001f)
            {
                var desiredExhaustDirection = -requestedForce.normalized;
                var exhaustDirection = ResolveAuxiliaryVisualDirection(
                    emitter,
                    requestedForce);
                var alignment = Vector3.Dot(exhaustDirection, desiredExhaustDirection);
                emitter.LastAlignment = alignment;
                var alignedOutput = Mathf.InverseLerp(
                    auxiliaryAlignmentThreshold,
                    1f,
                    alignment);
                var forceOutput = Mathf.Sqrt(outputFraction);
                var nozzleOutputScale = IsCantedArmNozzle(emitter)
                    ? 1.35f
                    : 1f;
                targetIntensity = Mathf.Clamp(
                    alignedOutput * forceOutput * nozzleOutputScale,
                    0.06f,
                    1.65f);
            }

            if (editorPreview)
            {
                targetIntensity = Mathf.Max(targetIntensity, 0.72f);
            }

            emitter.Intensity = Mathf.MoveTowards(
                emitter.Intensity,
                targetIntensity,
                auxiliaryResponseSpeed * responseDeltaTime);
            var visible = emitter.Intensity > 0.002f;
            emitter.SetVisible(visible);
            if (!visible)
            {
                return;
            }

            SetAuxiliaryEmitterPose(emitter, requestedForce);

            var rated = Mathf.Clamp01(emitter.Intensity);
            var overdrive = Mathf.Max(0f, emitter.Intensity - 1f);
            var flicker = 0.94f + Mathf.PerlinNoise(emitter.Seed, Time.time * 21f) * 0.12f;
            var length = Mathf.Lerp(auxiliaryMinimumLength, auxiliaryRatedLength, rated) *
                         (1f + overdrive * 0.3f) * flicker * emitter.LengthScale;
            var radius = auxiliaryNozzleRadius * Mathf.Lerp(0.55f, 1f, rated) *
                         (1f + overdrive * 0.12f) * emitter.RadiusScale;

            SetLayer(
                emitter.Outer,
                emitter.OuterTransform,
                emitter.OuterBlock,
                radius * 1.5f,
                length,
                emitter.Intensity,
                0.2f,
                1.7f,
                22f,
                edgeColor * 0.65f,
                edgeColor,
                emitter.Seed);
            SetLayer(
                emitter.Core,
                emitter.CoreTransform,
                emitter.CoreBlock,
                radius * 0.58f,
                length * 0.7f,
                emitter.Intensity,
                0.68f,
                3.6f,
                29f,
                coreColor,
                edgeColor,
                emitter.Seed + 3.17f);
            SetLayer(
                emitter.NozzleGlow,
                emitter.NozzleGlowTransform,
                emitter.NozzleGlowBlock,
                radius,
                Mathf.Max(0.1f, radius * 0.9f),
                emitter.Intensity,
                0.78f,
                2.2f,
                15f,
                coreColor,
                edgeColor,
                emitter.Seed + 8.31f);
        }

        private void SetAuxiliaryEmitterPose(
            PlumeEmitter emitter,
            Vector3 requestedForce = default)
        {
            if (emitter?.AuxiliaryAnchor == null || emitter.Root == null)
            {
                return;
            }

            var direction = ResolveAuxiliaryVisualDirection(emitter, requestedForce);
            var up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.95f
                ? transform.right
                : Vector3.up;
            emitter.Root.SetPositionAndRotation(
                emitter.AuxiliaryAnchor.TransformPoint(emitter.LocalPositionOffset),
                Quaternion.LookRotation(direction, up));
        }

        private Vector3 ResolveAuxiliaryVisualDirection(
            PlumeEmitter emitter,
            Vector3 requestedForce)
        {
            if (requestedForce.sqrMagnitude > 0.0001f)
            {
                return -requestedForce.normalized;
            }

            if (emitter.UsesManualTransform)
            {
                return (emitter.AuxiliaryAnchor.rotation *
                        Quaternion.Euler(emitter.LocalEulerOffset) *
                        Vector3.forward).normalized;
            }

            if (emitter.ShoulderDirection != ShoulderNozzleDirection.None)
            {
                var radial = emitter.AuxiliaryAnchor.position - transform.position;
                var sideSign = Mathf.Sign(Vector3.Dot(radial, transform.right));
                if (Mathf.Abs(sideSign) <= Mathf.Epsilon)
                {
                    sideSign = emitter.AnchorName.EndsWith(
                        "_dmy_8",
                        StringComparison.OrdinalIgnoreCase)
                        ? 1f
                        : -1f;
                }

                var outward = transform.right * sideSign;
                switch (emitter.ShoulderDirection)
                {
                    case ShoulderNozzleDirection.ForwardDown:
                        return (outward + transform.forward * 0.28f - transform.up * 0.18f)
                            .normalized;
                    case ShoulderNozzleDirection.RearwardDown:
                        return (outward - transform.forward * 0.28f - transform.up * 0.18f)
                            .normalized;
                    default:
                        return outward;
                }
            }

            var anchorDirection = emitter.AuxiliaryAnchor.forward.normalized;
            if (!IsCantedArmNozzle(emitter) ||
                requestedForce.sqrMagnitude <= 0.0001f)
            {
                return anchorDirection;
            }

            // The imported canted dummy axes sit slightly tangential to the visible round ports.
            // Preserve most of the authored angle while pulling the plume toward the commanded
            // exhaust direction enough to keep it centered and legible.
            return Vector3.Slerp(
                anchorDirection,
                -requestedForce.normalized,
                0.22f).normalized;
        }

        private static bool IsCantedArmNozzle(PlumeEmitter emitter)
        {
            if (emitter.AuxiliaryAnchor == null ||
                (emitter.AuxiliarySlot != MechPartSlot.Arms &&
                 emitter.AuxiliarySlot != MechPartSlot.LeftArm &&
                 emitter.AuxiliarySlot != MechPartSlot.RightArm))
            {
                return false;
            }

            return emitter.AnchorName.EndsWith(
                       "_dmy_70",
                       StringComparison.OrdinalIgnoreCase) ||
                   emitter.AnchorName.EndsWith(
                       "_dmy_71",
                       StringComparison.OrdinalIgnoreCase);
        }

        private ShoulderNozzleDirection ResolveShoulderNozzleDirection(
            Transform anchor,
            MechPartSlot slot)
        {
            if (anchor == null ||
                (slot != MechPartSlot.Arms &&
                 slot != MechPartSlot.LeftArm &&
                 slot != MechPartSlot.RightArm))
            {
                return ShoulderNozzleDirection.None;
            }

            if (anchor.name.EndsWith("_dmy_7", StringComparison.OrdinalIgnoreCase) ||
                anchor.name.EndsWith("_dmy_8", StringComparison.OrdinalIgnoreCase))
            {
                return ShoulderNozzleDirection.Lateral;
            }

            if (!anchor.name.EndsWith("_dmy_70", StringComparison.OrdinalIgnoreCase) &&
                !anchor.name.EndsWith("_dmy_71", StringComparison.OrdinalIgnoreCase))
            {
                return ShoulderNozzleDirection.None;
            }

            var matchingCount = 0;
            var forwardPositionSum = 0f;
            var candidates = GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (!candidate.name.Equals(anchor.name, StringComparison.OrdinalIgnoreCase) ||
                    !TryResolveAuxiliaryNozzleSlot(candidate, out var candidateSlot) ||
                    candidateSlot != slot)
                {
                    continue;
                }

                forwardPositionSum += Vector3.Dot(
                    candidate.position - transform.position,
                    transform.forward);
                matchingCount++;
            }

            if (matchingCount < 2)
            {
                return ShoulderNozzleDirection.ForwardDown;
            }

            var averageForwardPosition = forwardPositionSum / matchingCount;
            var anchorForwardPosition = Vector3.Dot(
                anchor.position - transform.position,
                transform.forward);
            return anchorForwardPosition >= averageForwardPosition
                ? ShoulderNozzleDirection.ForwardDown
                : ShoulderNozzleDirection.RearwardDown;
        }

        internal static bool ShouldDriveAuxiliaryNozzle(
            MechPartSlot slot,
            string nodeName,
            MechThrusterRole role)
        {
            if (slot == MechPartSlot.Arms ||
                slot == MechPartSlot.LeftArm ||
                slot == MechPartSlot.RightArm)
            {
                if (nodeName.EndsWith("_dmy_7", StringComparison.OrdinalIgnoreCase) ||
                    nodeName.EndsWith("_dmy_8", StringComparison.OrdinalIgnoreCase))
                {
                    return role == MechThrusterRole.StrafePositive ||
                           role == MechThrusterRole.StrafeNegative ||
                           role == MechThrusterRole.ShoulderRcs;
                }

                // Arm ports point almost entirely sideways/fore-aft, so they are maneuvering
                // nozzles rather than vertical shoulder-lift outlets.
                return role == MechThrusterRole.Main ||
                       role == MechThrusterRole.Reverse ||
                       role == MechThrusterRole.StrafePositive ||
                       role == MechThrusterRole.StrafeNegative ||
                       role == MechThrusterRole.ShoulderRcs;
            }

            if (slot != MechPartSlot.Legs)
            {
                return true;
            }

            if (nodeName.EndsWith("_dmy_201", StringComparison.OrdinalIgnoreCase))
            {
                // Center pelvis outlet faces aft and supplements forward acceleration.
                return role == MechThrusterRole.Main;
            }

            if (nodeName.EndsWith("_dmy_830", StringComparison.OrdinalIgnoreCase) ||
                nodeName.EndsWith("_dmy_831", StringComparison.OrdinalIgnoreCase))
            {
                // The two rear-facing leg outlets provide the visible reverse burn.
                return role == MechThrusterRole.Reverse;
            }

            // The four canted leg outlets have both downward and lateral components.
            return role == MechThrusterRole.LegLift ||
                   role == MechThrusterRole.Descent ||
                   role == MechThrusterRole.StrafePositive ||
                   role == MechThrusterRole.StrafeNegative ||
                   role == MechThrusterRole.LegRcs;
        }

        private static void SetLayer(
            MeshRenderer renderer,
            Transform layer,
            MaterialPropertyBlock block,
            float radius,
            float length,
            float intensity,
            float opacity,
            float softness,
            float flowSpeed,
            Color core,
            Color edge,
            float seed)
        {
            layer.localPosition = Vector3.zero;
            layer.localRotation = Quaternion.identity;
            layer.localScale = new Vector3(radius, radius, length);

            renderer.GetPropertyBlock(block);
            block.SetColor(CoreColorId, core);
            block.SetColor(EdgeColorId, edge);
            block.SetFloat(IntensityId, intensity);
            block.SetFloat(OpacityId, opacity);
            block.SetFloat(SoftnessId, softness);
            block.SetFloat(FlowSpeedId, flowSpeed);
            block.SetFloat(SeedId, seed);
            renderer.SetPropertyBlock(block);
        }

        private PlumeEmitter CreateEmitter(
            string channelId,
            string rootPrefix = "Main_Thruster_Plume")
        {
            var rootObject = new GameObject($"{rootPrefix}_{channelId}");
            rootObject.transform.SetParent(transform, false);
            var emitter = new PlumeEmitter
            {
                Root = rootObject.transform,
                Seed = Mathf.Abs(channelId.GetHashCode() % 10000) * 0.0137f,
            };
            emitter.Outer = CreateLayer(rootObject.transform, "Outer Plume", out emitter.OuterTransform);
            emitter.Core = CreateLayer(rootObject.transform, "Core Plume", out emitter.CoreTransform);
            emitter.NozzleGlow = CreateLayer(rootObject.transform, "Nozzle Glow", out emitter.NozzleGlowTransform);
            emitter.SetVisible(false);
            return emitter;
        }

        private MeshRenderer CreateLayer(Transform parent, string layerName, out Transform layerTransform)
        {
            var layer = new GameObject(layerName, typeof(MeshFilter), typeof(MeshRenderer));
            layer.transform.SetParent(parent, false);
            layer.GetComponent<MeshFilter>().sharedMesh = plumeMesh;
            var renderer = layer.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = plumeMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            layerTransform = layer.transform;
            return renderer;
        }

        private void EnsureResources()
        {
            plumeMesh ??= BuildPlumeMesh(4, 8);
            if (plumeMaterial != null)
            {
                return;
            }

            var shader = Shader.Find("ACunicombat/Mech Thruster Plume");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
            }

            if (shader == null)
            {
                Debug.LogError("[MechMainThrusterPlume] No compatible plume shader was found.", this);
                return;
            }

            plumeMaterial = new Material(shader)
            {
                name = "Runtime Main Thruster Plume Material",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = (int)RenderQueue.Transparent + 20,
                enableInstancing = true,
            };
        }

        private static Mesh BuildPlumeMesh(int planes, int segments)
        {
            planes = Mathf.Max(2, planes);
            segments = Mathf.Max(2, segments);
            var verticesPerPlane = (segments + 1) * 2;
            var vertices = new Vector3[planes * verticesPerPlane];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[planes * segments * 6];

            for (var plane = 0; plane < planes; plane++)
            {
                var angle = Mathf.PI * plane / planes;
                var radial = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                var vertexOffset = plane * verticesPerPlane;
                var triangleOffset = plane * segments * 6;
                for (var segment = 0; segment <= segments; segment++)
                {
                    var t = segment / (float)segments;
                    var width = t < 0.18f
                        ? Mathf.Lerp(0.78f, 1f, t / 0.18f)
                        : Mathf.Lerp(1f, 0.42f, (t - 0.18f) / 0.82f);
                    var left = vertexOffset + segment * 2;
                    var right = left + 1;
                    vertices[left] = -radial * width + Vector3.forward * t;
                    vertices[right] = radial * width + Vector3.forward * t;
                    uv[left] = new Vector2(0f, t);
                    uv[right] = new Vector2(1f, t);

                    if (segment == segments)
                    {
                        continue;
                    }

                    var index = triangleOffset + segment * 6;
                    triangles[index] = left;
                    triangles[index + 1] = left + 2;
                    triangles[index + 2] = right;
                    triangles[index + 3] = right;
                    triangles[index + 4] = left + 2;
                    triangles[index + 5] = right + 2;
                }
            }

            var mesh = new Mesh
            {
                name = "Runtime Main Thruster Plume",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                uv = uv,
                triangles = triangles,
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDisable()
        {
            foreach (var pair in emitters)
            {
                pair.Value.SetVisible(false);
            }

            foreach (var pair in auxiliaryEmitters)
            {
                pair.Value.SetVisible(false);
            }
        }

        private void OnDestroy()
        {
            if (plumeMaterial != null) Destroy(plumeMaterial);
            if (plumeMesh != null) Destroy(plumeMesh);
            foreach (var pair in auxiliaryEmitters)
            {
                if (pair.Value.Root != null)
                {
                    Destroy(pair.Value.Root.gameObject);
                }
            }
            auxiliaryEmitters.Clear();
        }

        private sealed class PlumeEmitter
        {
            public MechThrusterChannelState Channel;
            public Transform AuxiliaryAnchor;
            public string AnchorName, MirrorAnchorName;
            public MechPartSlot AuxiliarySlot;
            public ShoulderNozzleDirection ShoulderDirection;
            public string LayoutId;
            public string SourceId;
            public string DisplayName;
            public bool UsesManualTransform;
            public Vector3 LocalPositionOffset;
            public Vector3 LocalEulerOffset;
            public float RadiusScale = 1f;
            public float LengthScale = 1f;
            public float RatedThrustKn = 900f;
            public bool VectoringEnabled;
            public float MaximumVectorAngleDegrees;
            public Transform Root;
            public Transform MainGimbalVisualRoot;
            public Transform MainGimbalParent;
            public Quaternion MainGimbalRestLocalRotation;
            public Vector3 MainGimbalRestExhaustInParent;
            public Transform OuterTransform;
            public Transform CoreTransform;
            public Transform NozzleGlowTransform;
            public MeshRenderer Outer;
            public MeshRenderer Core;
            public MeshRenderer NozzleGlow;
            public readonly MaterialPropertyBlock OuterBlock = new();
            public readonly MaterialPropertyBlock CoreBlock = new();
            public readonly MaterialPropertyBlock NozzleGlowBlock = new();
            public float Intensity;
            public float Seed;
            public bool SeenThisFrame;
            public float LastRequestedForceKn;
            public float LastAlignment = -1f;
            public bool Visible { get; private set; }

            public void SetVisible(bool value)
            {
                Visible = value;
                if (Outer != null) Outer.enabled = value;
                if (Core != null) Core.enabled = value;
                if (NozzleGlow != null) NozzleGlow.enabled = value;
            }
        }

        private enum ShoulderNozzleDirection
        {
            None,
            Lateral,
            ForwardDown,
            RearwardDown,
        }
    }
}
