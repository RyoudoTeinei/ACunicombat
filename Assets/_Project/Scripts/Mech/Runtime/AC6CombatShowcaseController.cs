using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Runs a compact combat-pipeline showcase over an assembled modular mech. Original HKX
    /// clips drive the frame and equipment. Root translation, target motion and projectile
    /// flight are deliberately authored here because those are gameplay simulation concerns,
    /// not baked animation data.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AC6CombatShowcaseController : MonoBehaviour
    {
        private const string DefaultIdleBodyActionId = "a000_000000";
        private const string DefaultForwardBoostBodyActionId = "a000_120200";
        private const string DefaultLeftQuickBoostBodyActionId = "a000_028002";
        private const string DefaultLeftQuickBoostBrakeBodyActionId = "a000_004242";
        private const string DefaultRightQuickBoostBodyActionId = "a000_028003";
        private const string DefaultRightQuickBoostBrakeBodyActionId = "a000_004243";
        private const string DefaultRecoveryBodyActionId = "a000_000000";
        private const string DefaultRangedRaiseUpperBodyActionId = "a200_030020";
        private const string DefaultRangedHoldUpperBodyActionId = "a200_030022";
        private const string DefaultRangedFireUpperBodyActionId = "a200_030030";

        private static readonly string[] DefaultUpperBodyBoneNames =
        {
            "Spine1", "Neck", "Offset_ClavicleLink", "Spine1_EX", "Head",
            "L_ClavicleLink", "R_ClavicleLink", "L_ClavicleLink_EX", "Offset_L_Clavicle",
            "Offset_R_Clavicle", "R_ClavicleLink_EX", "L_Clavicle", "R_Clavicle",
            "L_ArmJoint", "R_ArmJoint", "L_ClavicleEX_A", "L_ClavicleEX_B",
            "Root_L_UpperArm", "R_ClavicleEX_A", "R_ClavicleEX_B", "Root_R_UpperArm",
            "L_UpperArm", "R_UpperArm", "L_ForeArm", "L_UpArmEX_A", "L_UpArmEX_B",
            "L_UpArmTwist", "R_ForeArm", "R_UpArmEX_A", "R_UpArmEX_B", "R_UpArmTwist",
            "L_Elbow", "L_ForeArmTwist", "L_HandTwist", "L_Wrist", "R_Elbow",
            "R_ForeArmTwist", "R_HandTwist", "R_Wrist", "Root_L_Hand", "Root_R_Hand",
            "L_Hand", "R_Hand", "L_Finger0", "L_Finger1", "L_Finger2", "L_Finger3",
            "L_Finger4", "L_Weapon_00", "R_Finger0", "R_Finger1", "R_Finger2",
            "R_Finger3", "R_Finger4", "R_Weapon_00", "L_Finger01", "L_Finger11",
            "L_Finger21", "L_Finger31", "L_Finger41", "R_Finger01", "R_Finger11",
            "R_Finger21", "R_Finger31", "R_Finger41", "L_Finger02", "L_Finger12",
            "L_Finger22", "L_Finger32", "L_Finger42", "R_Finger02", "R_Finger12",
            "R_Finger22", "R_Finger32", "R_Finger42"
        };

        private enum ShowcasePhase
        {
            Standby,
            ForwardAssaultBoost,
            LeftQuickBoost,
            RightQuickBoost,
            RaiseAndFire,
            Recovery
        }

        [SerializeField] private GameObject mech;
        [SerializeField] private Transform target;
        [SerializeField] private Camera showcaseCamera;
        [SerializeField] private MechActionEffectTimeline bodyEffectTimeline;
        [SerializeField] private MechActionEffectTimeline meleeEffectTimeline;
        [SerializeField] private MechEffectPrefabLibrary effectLibrary;
        [SerializeField, Min(2f)] private float phaseDuration = 3.5f;
        [SerializeField] private bool loop = true;
        [Tooltip("外部控制模式：保留 Start() 的部件/动作/特效装配，但不进入演示阶段循环，" +
                 "动作选择交给行为图解释器 + 玩家输入（MechPlayerRig）。")]
        [SerializeField] private bool externalControlMode;

        [Header("Body actions (optional until the TAE action table is confirmed)")]
        [SerializeField] private MechActionRegistry actionRegistry;
        [SerializeField] private MechBodyActionPlayer bodyActionPlayer;
        [SerializeField] private List<string> upperBodyBoneNames = new List<string>();
        [SerializeField] private string idleBodyActionId = DefaultIdleBodyActionId;
        [SerializeField] private string forwardBoostBodyActionId = DefaultForwardBoostBodyActionId;
        [SerializeField] private string leftQuickBoostBodyActionId = DefaultLeftQuickBoostBodyActionId;
        [SerializeField] private string leftQuickBoostBrakeBodyActionId = DefaultLeftQuickBoostBrakeBodyActionId;
        [SerializeField] private string rightQuickBoostBodyActionId = DefaultRightQuickBoostBodyActionId;
        [SerializeField] private string rightQuickBoostBrakeBodyActionId = DefaultRightQuickBoostBrakeBodyActionId;
        [SerializeField] private string recoveryBodyActionId = DefaultRecoveryBodyActionId;
        [SerializeField] private string rangedRaiseUpperBodyActionId = "a200_030020";
        [SerializeField] private string rangedHoldUpperBodyActionId = "a200_030022";
        [SerializeField] private string rangedFireUpperBodyActionId = "a200_030030";

        private readonly List<ParticleSystem> boosterExhausts = new List<ParticleSystem>();
        private readonly List<MechEquipmentAnimationPlayer> boosterPlayers =
            new List<MechEquipmentAnimationPlayer>();
        private readonly List<MechEquipmentAnimationPlayer> missilePlayers =
            new List<MechEquipmentAnimationPlayer>();

        private MechRuntimeState runtimeState;
        private MechSynchronizedAnimationPlayer bodyPlayer;
        private MechEquipmentAnimationPlayer rightHandWeaponPlayer;
        private MechEquipmentAnimationPlayer pileBunkerPlayer;
        private MechActionEffectPlayer pileBunkerEffects;
        private Vector3 origin;
        private float elapsed;
        private int activePhase = -1;
        private int launchedMissiles;
        private int materialsWithBaseMap;
        private int materialCount;
        private bool quickBoostBrakePlayed;
        private bool rangedHoldPlayed;
        private bool rangedFirePlayed;
        private bool rangedLayerCleared;
        private bool integratedRadarHudPresent;
        private readonly HashSet<string> playedBodyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> playedUpperBodyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PoseProbe> poseProbes = new List<PoseProbe>();
        private readonly Dictionary<Transform, Quaternion> phaseStartRotations =
            new Dictionary<Transform, Quaternion>();
        private readonly Dictionary<ShowcasePhase, float> phasePoseDeltas =
            new Dictionary<ShowcasePhase, float>();

        public string CurrentPhaseName => ((ShowcasePhase)Mathf.Max(0, activePhase)).ToString();
        public int LaunchedMissiles => launchedMissiles;
        public bool IsBodyActionGraphValid => bodyActionPlayer != null && bodyActionPlayer.IsGraphValid;
        public int BodyActionProbeCount => poseProbes.Count;
        public MechActionRegistry ActionRegistry => actionRegistry;
        public IReadOnlyList<string> UpperBodyBoneNames => upperBodyBoneNames;

        public void Configure(
            GameObject mechRoot,
            Transform movingTarget,
            Camera sceneCamera,
            MechActionEffectTimeline bodyTimeline,
            MechActionEffectTimeline meleeTimeline,
            MechEffectPrefabLibrary library)
        {
            mech = mechRoot;
            target = movingTarget;
            showcaseCamera = sceneCamera;
            bodyEffectTimeline = bodyTimeline;
            meleeEffectTimeline = meleeTimeline;
            effectLibrary = library;
        }

        public void ConfigureBodyActions(
            MechActionRegistry registry,
            IEnumerable<string> maskedUpperBodyBoneNames,
            string idleActionId,
            string forwardBoostActionId,
            string leftQuickBoostActionId,
            string leftQuickBoostBrakeActionId,
            string rightQuickBoostActionId,
            string rightQuickBoostBrakeActionId,
            string recoveryActionId,
            string rangedRaiseActionId,
            string rangedHoldActionId,
            string rangedFireActionId)
        {
            actionRegistry = registry;
            upperBodyBoneNames = maskedUpperBodyBoneNames?
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();
            idleBodyActionId = idleActionId;
            forwardBoostBodyActionId = forwardBoostActionId;
            leftQuickBoostBodyActionId = leftQuickBoostActionId;
            leftQuickBoostBrakeBodyActionId = leftQuickBoostBrakeActionId;
            rightQuickBoostBodyActionId = rightQuickBoostActionId;
            rightQuickBoostBrakeBodyActionId = rightQuickBoostBrakeActionId;
            recoveryBodyActionId = recoveryActionId;
            rangedRaiseUpperBodyActionId = rangedRaiseActionId;
            rangedHoldUpperBodyActionId = rangedHoldActionId;
            rangedFireUpperBodyActionId = rangedFireActionId;
        }

        private void Start()
        {
            if (mech == null)
            {
                mech = gameObject;
            }

            origin = mech.transform.position;
            BindAssembledParts();
            ConfigureBodyActionsRuntime();
            ConfigureBodyEffects();
            BuildPoseProbes();
            BuildBoosterExhausts();
            ValidateExhaustAxes();
            CountMaterialBindings();
            integratedRadarHudPresent =
                mech.GetComponent<MechRadarHudPresenter>() != null ||
                GetComponentInChildren<MechRadarHudPresenter>(true) != null;
            Debug.Log(
                $"[AC6CombatShowcase] Bindings: boosters={boosterPlayers.Count}, " +
                $"missilePods={missilePlayers.Count}, rightHand={(rightHandWeaponPlayer != null ? 1 : 0)}, " +
                $"pileBunker={(pileBunkerPlayer != null ? 1 : 0)}, " +
                $"exhausts={boosterExhausts.Count}, fxrPrefabs={(effectLibrary == null ? 0 : effectLibrary.entries.Count)}.",
                mech);
            if (externalControlMode)
            {
                return; // 装配完成即退出：不进入演示阶段，动作由外部行为图驱动。
            }

            EnterPhase(0);
        }

        /// <summary>外部控制模式开关（玩家控制场景构建器写入）。</summary>
        public void SetExternalControlMode(bool value) => externalControlMode = value;

        /// <summary>Start() 装配出的动作播放器（外部控制模式下由 MechPlayerRig 消费）。</summary>
        public MechBodyActionPlayer ConfiguredBodyActionPlayer => bodyActionPlayer;

        private void Update()
        {
            if (externalControlMode)
            {
                return;
            }

            var phaseCount = Enum.GetValues(typeof(ShowcasePhase)).Length;
            elapsed += Time.deltaTime;
            var phase = Mathf.FloorToInt(elapsed / phaseDuration);
            if (phase >= phaseCount)
            {
                if (!loop)
                {
                    phase = phaseCount - 1;
                    elapsed = phase * phaseDuration + phaseDuration;
                }
                else
                {
                    elapsed %= phaseCount * phaseDuration;
                    phase = 0;
                    launchedMissiles = 0;
                }
            }

            if (phase != activePhase)
            {
                EnterPhase(phase);
            }

            var phaseTime = elapsed - phase * phaseDuration;
            var normalized = Mathf.Clamp01(phaseTime / phaseDuration);
            ApplyPhaseActions((ShowcasePhase)phase, normalized);
            ApplyMotion((ShowcasePhase)phase, normalized);
            SamplePhasePose((ShowcasePhase)phase);
            AnimateTarget();
        }

        private void LateUpdate()
        {
            if (externalControlMode || showcaseCamera == null || mech == null)
            {
                return;
            }

            var focus = mech.transform.position + Vector3.up * 4.8f;
            var desired = mech.transform.position + new Vector3(13f, 8.5f, 20f);
            showcaseCamera.transform.position = Vector3.Lerp(
                showcaseCamera.transform.position,
                desired,
                1f - Mathf.Exp(-3f * Time.deltaTime));
            showcaseCamera.transform.rotation = Quaternion.LookRotation(
                focus - showcaseCamera.transform.position,
                Vector3.up);
        }

        private void BindAssembledParts()
        {
            runtimeState = mech.GetComponent<MechRuntimeState>();
            bodyPlayer = mech.GetComponent<MechSynchronizedAnimationPlayer>();
            boosterPlayers.Clear();
            missilePlayers.Clear();

            if (runtimeState == null)
            {
                Debug.LogError("[AC6CombatShowcase] MechRuntimeState is missing.", mech);
                return;
            }

            for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
            {
                var record = runtimeState.AssembledParts[i];
                if (record?.instance == null)
                {
                    continue;
                }

                var player = record.instance.GetComponent<MechEquipmentAnimationPlayer>();
                if (record.slot == MechPartSlot.Booster)
                {
                    boosterPlayers.AddRange(mech.GetComponentsInChildren<MechEquipmentAnimationPlayer>(true)
                        .Where(candidate => candidate != null &&
                            candidate.gameObject.name.IndexOf("Booster", StringComparison.OrdinalIgnoreCase) >= 0));
                }
                else if (record.slot == MechPartSlot.LeftBackWeapon ||
                         record.slot == MechPartSlot.RightBackWeapon)
                {
                    if (player != null && player.HasAction("a000_800410"))
                    {
                        missilePlayers.Add(player);
                    }
                }
                else if (record.slot == MechPartSlot.RightHandWeapon && player != null)
                {
                    rightHandWeaponPlayer = player;
                }
                else if (record.slot == MechPartSlot.LeftHandWeapon &&
                         player != null && player.HasAction("a000_040010"))
                {
                    pileBunkerPlayer = player;
                    if (meleeEffectTimeline != null)
                    {
                        pileBunkerEffects = record.instance.GetComponent<MechActionEffectPlayer>();
                        if (pileBunkerEffects == null)
                        {
                            pileBunkerEffects = record.instance.AddComponent<MechActionEffectPlayer>();
                        }

                        pileBunkerEffects.ConfigureForEquipment(
                            meleeEffectTimeline,
                            record.instance.transform,
                            pileBunkerPlayer,
                            effectLibrary);
                    }
                }
            }

            boosterPlayers.RemoveAll(candidate => candidate == null);
            for (var i = boosterPlayers.Count - 1; i >= 0; i--)
            {
                if (boosterPlayers.IndexOf(boosterPlayers[i]) != i)
                {
                    boosterPlayers.RemoveAt(i);
                }
            }
        }

        private void ConfigureBodyEffects()
        {
            // The legacy body-effect player owns a MechSynchronizedAnimationPlayer graph.
            // Running it beside MechBodyActionPlayer makes both graphs drive the same Animators.
            if (bodyActionPlayer != null && bodyActionPlayer.IsGraphValid)
            {
                var legacyEffectPlayer = mech.GetComponent<MechActionEffectPlayer>();
                if (legacyEffectPlayer != null)
                {
                    legacyEffectPlayer.enabled = false;
                }
                return;
            }

            if (bodyEffectTimeline == null || runtimeState == null || bodyPlayer == null)
            {
                return;
            }

            var player = mech.GetComponent<MechActionEffectPlayer>();
            if (player == null)
            {
                player = mech.AddComponent<MechActionEffectPlayer>();
            }

            player.Configure(bodyEffectTimeline, runtimeState, bodyPlayer, effectLibrary);
            bodyPlayer.Play();
        }

        private void EnterPhase(int phase)
        {
            FinalizePhasePose();
            activePhase = phase;
            var state = (ShowcasePhase)phase;
            quickBoostBrakePlayed = false;
            rangedHoldPlayed = false;
            rangedFirePlayed = false;
            rangedLayerCleared = false;
            CapturePhasePoseStart();
            switch (state)
            {
                case ShowcasePhase.Standby:
                    ClearUpperBodyAction();
                    PlayBodyAction(idleBodyActionId);
                    SetBoosterExhaust(false, 0f);
                    break;
                case ShowcasePhase.ForwardAssaultBoost:
                    PlayBoosterAction("a000_000001");
                    PlayBodyAction(forwardBoostBodyActionId);
                    SetBoosterExhaust(true, 1.5f);
                    SpawnBurstAtBoosters(new Color(0.25f, 0.65f, 1f, 1f), 0.9f);
                    break;
                case ShowcasePhase.LeftQuickBoost:
                    PlayBoosterAction("a000_000002");
                    PlayBodyAction(leftQuickBoostBodyActionId);
                    SetBoosterExhaust(true, 2.2f);
                    SpawnBurstAtBoosters(new Color(0.25f, 0.65f, 1f, 1f), 1.2f);
                    break;
                case ShowcasePhase.RightQuickBoost:
                    PlayBoosterAction("a000_000002");
                    PlayBodyAction(rightQuickBoostBodyActionId);
                    SetBoosterExhaust(true, 2.2f);
                    SpawnBurstAtBoosters(new Color(0.25f, 0.65f, 1f, 1f), 1.2f);
                    break;
                case ShowcasePhase.RaiseAndFire:
                    PlayBodyAction(idleBodyActionId);
                    SetBoosterExhaust(true, 0.35f);
                    PlayUpperBodyAction(rangedRaiseUpperBodyActionId);
                    break;
                case ShowcasePhase.Recovery:
                    ClearUpperBodyAction();
                    PlayBodyAction(recoveryBodyActionId);
                    PlayBoosterAction("a000_000003");
                    SetBoosterExhaust(false, 0f);
                    break;
            }

            Debug.Log($"[AC6CombatShowcase] Phase {phase + 1}: {state}", mech);
        }

        private void ApplyPhaseActions(ShowcasePhase phase, float normalized)
        {
            if (phase == ShowcasePhase.LeftQuickBoost &&
                !quickBoostBrakePlayed && normalized >= 0.22f)
            {
                quickBoostBrakePlayed = PlayBodyAction(leftQuickBoostBrakeBodyActionId);
            }
            else if (phase == ShowcasePhase.RightQuickBoost &&
                     !quickBoostBrakePlayed && normalized >= 0.22f)
            {
                quickBoostBrakePlayed = PlayBodyAction(rightQuickBoostBrakeBodyActionId);
            }

            if (phase != ShowcasePhase.RaiseAndFire)
            {
                return;
            }

            // The raise clip is only 0.267 seconds long. Transition before it fades itself out.
            if (!rangedHoldPlayed && normalized >= 0.06f)
            {
                rangedHoldPlayed = PlayUpperBodyAction(rangedHoldUpperBodyActionId);
            }
            if (!rangedFirePlayed && normalized >= 0.24f)
            {
                rangedFirePlayed = PlayUpperBodyAction(rangedFireUpperBodyActionId);
                FireRightHandWeapon();
            }
            if (!rangedLayerCleared && normalized >= 0.82f)
            {
                ClearUpperBodyAction();
                rangedLayerCleared = true;
            }
        }

        private void ConfigureBodyActionsRuntime()
        {
            RecoverMissingBodyActionConfiguration();
            if (actionRegistry == null || runtimeState == null)
            {
                Debug.LogWarning(
                    "[AC6CombatShowcase] Body action graph skipped: registry or runtime state is missing.",
                    mech);
                return;
            }

            if (bodyPlayer != null)
            {
                bodyPlayer.StopPlayback();
                bodyPlayer.enabled = false;
            }

            bodyActionPlayer = mech.GetComponent<MechBodyActionPlayer>();
            if (bodyActionPlayer == null)
            {
                bodyActionPlayer = mech.AddComponent<MechBodyActionPlayer>();
            }

            bodyActionPlayer.Configure(
                actionRegistry,
                runtimeState,
                upperBodyBoneNames,
                idleBodyActionId,
                1f);

            if (bodyActionPlayer.IsGraphValid &&
                string.Equals(
                    bodyActionPlayer.ActiveFullBodyActionId,
                    idleBodyActionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                playedBodyActions.Add(idleBodyActionId);
                Debug.Log($"[AC6CombatShowcase] Body action PASS: {idleBodyActionId}", mech);
            }

            Debug.Log(
                $"[AC6CombatShowcase] Body action graph: " +
                $"{(bodyActionPlayer.IsGraphValid ? "PASS" : "FAIL")} " +
                $"parts={bodyActionPlayer.PartCount} sharedBones={bodyActionPlayer.SharedBoneBindingCount} " +
                $"upperMaskBones={upperBodyBoneNames.Count}.",
                mech);
        }

        private void RecoverMissingBodyActionConfiguration()
        {
#if UNITY_EDITOR
            if (actionRegistry == null)
            {
                actionRegistry = UnityEditor.AssetDatabase.LoadAssetAtPath<MechActionRegistry>(
                    "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/AC6CombatShowcaseRegistry.asset");
            }
#endif
            if (upperBodyBoneNames == null)
            {
                upperBodyBoneNames = new List<string>();
            }
            if (upperBodyBoneNames.Count == 0)
            {
                upperBodyBoneNames.AddRange(DefaultUpperBodyBoneNames);
            }

            idleBodyActionId = DefaultActionId(idleBodyActionId, DefaultIdleBodyActionId);
            forwardBoostBodyActionId = DefaultActionId(
                forwardBoostBodyActionId, DefaultForwardBoostBodyActionId);
            leftQuickBoostBodyActionId = DefaultActionId(
                leftQuickBoostBodyActionId, DefaultLeftQuickBoostBodyActionId);
            leftQuickBoostBrakeBodyActionId = DefaultActionId(
                leftQuickBoostBrakeBodyActionId, DefaultLeftQuickBoostBrakeBodyActionId);
            rightQuickBoostBodyActionId = DefaultActionId(
                rightQuickBoostBodyActionId, DefaultRightQuickBoostBodyActionId);
            rightQuickBoostBrakeBodyActionId = DefaultActionId(
                rightQuickBoostBrakeBodyActionId, DefaultRightQuickBoostBrakeBodyActionId);
            recoveryBodyActionId = DefaultActionId(recoveryBodyActionId, DefaultRecoveryBodyActionId);
            rangedRaiseUpperBodyActionId = DefaultActionId(
                rangedRaiseUpperBodyActionId, DefaultRangedRaiseUpperBodyActionId);
            rangedHoldUpperBodyActionId = DefaultActionId(
                rangedHoldUpperBodyActionId, DefaultRangedHoldUpperBodyActionId);
            rangedFireUpperBodyActionId = DefaultActionId(
                rangedFireUpperBodyActionId, DefaultRangedFireUpperBodyActionId);
        }

        private static string DefaultActionId(string configured, string fallback) =>
            string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        private void ApplyMotion(ShowcasePhase phase, float t)
        {
            Vector3 localOffset;
            float pitch;
            float roll;
            switch (phase)
            {
                case ShowcasePhase.Standby:
                    localOffset = new Vector3(0f, Mathf.SmoothStep(0f, 3f, t), 0f);
                    pitch = -4f * t;
                    roll = 0f;
                    break;
                case ShowcasePhase.ForwardAssaultBoost:
                    localOffset = new Vector3(0f, 3f + Mathf.Sin(t * Mathf.PI) * 0.35f, -18f * t);
                    pitch = -11f;
                    roll = 0f;
                    break;
                case ShowcasePhase.LeftQuickBoost:
                    localOffset = new Vector3(Mathf.SmoothStep(0f, -10f, t), 3f, -18f - 6f * t);
                    pitch = -7f;
                    roll = -9f * Mathf.Sin(t * Mathf.PI);
                    break;
                case ShowcasePhase.RightQuickBoost:
                    localOffset = new Vector3(Mathf.SmoothStep(-10f, 0f, t), 3f, -24f - 6f * t);
                    pitch = -7f;
                    roll = 9f * Mathf.Sin(t * Mathf.PI);
                    break;
                case ShowcasePhase.RaiseAndFire:
                    localOffset = new Vector3(0f, 3f + Mathf.Sin(t * Mathf.PI * 2f) * 0.15f, -30f);
                    pitch = -3f;
                    roll = 0f;
                    break;
                default:
                    localOffset = Vector3.Lerp(new Vector3(0f, 3f, -30f), Vector3.zero, Smooth01(t));
                    pitch = Mathf.Lerp(-3f, 0f, t);
                    roll = 0f;
                    break;
            }

            mech.transform.position = origin + localOffset;
            mech.transform.rotation = Quaternion.Euler(pitch, 0f, roll);
        }

        private void AnimateTarget()
        {
            if (target == null || mech == null)
            {
                return;
            }

            var time = Time.time;
            target.position = mech.transform.position + new Vector3(
                Mathf.Sin(time * 0.45f) * 8f,
                5f + Mathf.Sin(time * 0.8f) * 2f,
                -34f + Mathf.Cos(time * 0.45f) * 5f);
            target.Rotate(25f * Time.deltaTime, 40f * Time.deltaTime, 15f * Time.deltaTime, Space.Self);
        }

        private void PlayBoosterAction(string actionId)
        {
            for (var i = 0; i < boosterPlayers.Count; i++)
            {
                if (boosterPlayers[i].HasAction(actionId))
                {
                    boosterPlayers[i].PlayAction(actionId);
                }
            }
        }

        private void FireMissileSalvo()
        {
            var before = launchedMissiles;
            for (var i = 0; i < missilePlayers.Count; i++)
            {
                var player = missilePlayers[i];
                var actionId = FirstAvailableAction(player,
                    "a000_800410", "a000_800411", "a000_800412", "a000_000100");
                if (!string.IsNullOrEmpty(actionId))
                {
                    player.PlayAction(actionId);
                }

                var anchors = player.GetComponentsInChildren<Transform>(true)
                    .Where(candidate => candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase)
                        || candidate.name.EndsWith("_dmy_301", StringComparison.OrdinalIgnoreCase)
                        || candidate.name.EndsWith("_dmy_302", StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .ToArray();
                if (anchors.Length == 0)
                {
                    anchors = new[] { player.transform };
                }

                for (var anchorIndex = 0; anchorIndex < anchors.Length; anchorIndex++)
                {
                    SpawnMissile(anchors[anchorIndex], i * 0.12f + anchorIndex * 0.08f);
                }
            }

            Debug.Log(
                $"[AC6CombatShowcase] Missile salvo spawned {launchedMissiles - before} projectiles " +
                $"from {missilePlayers.Count} original-HKX equipment players.",
                mech);
        }

        private void SpawnMissile(Transform anchor, float launchDelay)
        {
            var missile = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            missile.name = "Showcase_Plasma_Missile";
            missile.transform.position = anchor.position;
            missile.transform.localScale = new Vector3(0.12f, 0.32f, 0.12f);

            var collider = missile.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var renderer = missile.GetComponent<Renderer>();
            renderer.sharedMaterial = CreateRuntimeMaterial(
                "Showcase Missile",
                new Color(0.15f, 0.45f, 1f, 1f),
                true);

            var trail = missile.AddComponent<TrailRenderer>();
            trail.time = 0.65f;
            trail.startWidth = 0.18f;
            trail.endWidth = 0.01f;
            trail.minVertexDistance = 0.04f;
            trail.sharedMaterial = CreateRuntimeMaterial(
                "Showcase Missile Trail",
                new Color(0.2f, 0.7f, 1f, 0.85f),
                true);

            var mover = missile.AddComponent<AC6ShowcaseMissile>();
            mover.Configure(target, 22f, 5f, launchDelay);
            SpawnBurst(anchor, new Color(0.25f, 0.7f, 1f, 1f), 0.55f);
            launchedMissiles++;
        }

        private void FireRightHandWeapon()
        {
            if (rightHandWeaponPlayer == null)
            {
                Debug.LogWarning("[AC6CombatShowcase] Ranged weapon fire skipped: no right-hand equipment player.", mech);
                return;
            }

            var actionId = FirstAvailableAction(
                rightHandWeaponPlayer,
                "a000_800210",
                "a000_800090",
                "a000_800000");
            if (!string.IsNullOrEmpty(actionId))
            {
                rightHandWeaponPlayer.PlayAction(actionId);
            }

            var muzzle = rightHandWeaponPlayer.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(candidate =>
                    candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase) ||
                    candidate.name.EndsWith("_dmy_301", StringComparison.OrdinalIgnoreCase) ||
                    candidate.name.EndsWith("_dmy_100", StringComparison.OrdinalIgnoreCase));
            SpawnBurst(
                muzzle != null ? muzzle : rightHandWeaponPlayer.transform,
                new Color(1f, 0.48f, 0.08f, 1f),
                1.45f);
            Debug.Log($"[AC6CombatShowcase] Ranged weapon fired action {actionId}.", mech);
        }

        private void BuildBoosterExhausts()
        {
            boosterExhausts.Clear();
            for (var i = 0; i < boosterPlayers.Count; i++)
            {
                var anchors = boosterPlayers[i].GetComponentsInChildren<Transform>(true)
                    .Where(candidate => candidate.name.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                for (var anchorIndex = 0; anchorIndex < anchors.Length; anchorIndex++)
                {
                    boosterExhausts.Add(CreateExhaust(anchors[anchorIndex]));
                }
            }
        }

        private ParticleSystem CreateExhaust(Transform anchor)
        {
            var root = new GameObject("Showcase_Booster_Exhaust");
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = Vector3.zero;
            // The flame dummy's +Z IS the exhaust direction since the dummy-axis exporter fix
            // (a1bdac6): the sample builder asserts flame.forward points backward and passes.
            // An extra local yaw here double-flips on the corrected data and fires the plume
            // into the hull, so the emitter inherits the dummy frame unchanged.
            root.transform.localRotation = Quaternion.identity;
            var particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.duration = 1f;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = 0.24f;
            main.startSpeed = 10f;
            main.startSize = 0.24f;
            main.startColor = new Color(0.25f, 0.65f, 1f, 0.95f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = particles.emission;
            emission.rateOverTime = 80f;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 6f;
            shape.radius = 0.06f;

            var color = particles.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.1f, 0.45f, 1f), 0.35f),
                    new GradientColorKey(new Color(0.04f, 0.1f, 0.35f), 1f)
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });

            var particleRenderer = root.GetComponent<ParticleSystemRenderer>();
            particleRenderer.sharedMaterial = CreateRuntimeMaterial(
                "Showcase Booster Exhaust",
                new Color(0.2f, 0.65f, 1f, 1f),
                true);
            return particles;
        }

        /// <summary>
        /// Final-geometry assertion for the exhaust plumes (acceptance items 5/7 of the
        /// showcase consult): every emitter's world +Z must point backward relative to the
        /// mech (which faces -Z, same convention as the sample builder) and away from the
        /// core, so a regression anywhere in the dummy/install chain fails loudly instead
        /// of rendering flames inside the hull. Batch validation keys on the PASS/FAIL log.
        /// </summary>
        private void ValidateExhaustAxes()
        {
            var corePosition = mech.transform.position;
            if (runtimeState != null)
            {
                for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
                {
                    var record = runtimeState.AssembledParts[i];
                    if (record?.instance != null && record.slot == MechPartSlot.Core)
                    {
                        corePosition = record.instance.transform.position;
                        break;
                    }
                }
            }

            var back = mech.transform.forward;
            var pass = boosterExhausts.Count > 0;
            for (var i = 0; i < boosterExhausts.Count; i++)
            {
                var emitter = boosterExhausts[i].transform;
                var axis = emitter.forward;
                var backDot = Vector3.Dot(axis, back);
                var startDistance = Vector3.Distance(emitter.position, corePosition);
                var plumeDistance = Vector3.Distance(emitter.position + axis * 2f, corePosition);
                var distanceGain = plumeDistance - startDistance;
                var ok = backDot > 0.6f && distanceGain > 0.05f;
                pass &= ok;
                Debug.Log(
                    $"[AC6CombatShowcase] Exhaust {i}: axis={axis:F3} backDot={backDot:0.###} " +
                    $"plumeDistanceGain={distanceGain:0.###}m {(ok ? "ok" : "WRONG")}",
                    emitter);
            }

            Debug.Log(
                $"[AC6CombatShowcase] Exhaust axis check: {(pass ? "PASS" : "FAIL")} " +
                $"({boosterExhausts.Count} emitters).",
                mech);
        }

        private bool PlayBodyAction(string actionId)
        {
            if (bodyActionPlayer == null || string.IsNullOrWhiteSpace(actionId))
            {
                return false;
            }

            var played = bodyActionPlayer.PlayFullBody(actionId);
            if (played && playedBodyActions.Add(actionId))
            {
                Debug.Log($"[AC6CombatShowcase] Body action PASS: {actionId}", mech);
            }
            else if (!played)
            {
                Debug.LogError($"[AC6CombatShowcase] Body action FAIL: {actionId}", mech);
            }
            return played;
        }

        private bool PlayUpperBodyAction(string actionId)
        {
            if (bodyActionPlayer == null || string.IsNullOrWhiteSpace(actionId))
            {
                return false;
            }

            var played = bodyActionPlayer.PlayUpperBody(actionId);
            if (played && playedUpperBodyActions.Add(actionId))
            {
                Debug.Log($"[AC6CombatShowcase] Upper-body action PASS: {actionId}", mech);
            }
            else if (!played)
            {
                Debug.LogError($"[AC6CombatShowcase] Upper-body action FAIL: {actionId}", mech);
            }
            return played;
        }

        private void ClearUpperBodyAction()
        {
            bodyActionPlayer?.ClearUpperBody(0.12f);
        }

        private void BuildPoseProbes()
        {
            poseProbes.Clear();
            if (runtimeState == null)
            {
                return;
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal)
            {
                "Spine1", "Neck", "L_Clavicle", "R_Clavicle", "L_UpperArm", "R_UpperArm",
                "L_ForeArm", "R_ForeArm", "L_Wrist", "R_Wrist", "L_Thigh", "R_Thigh",
                "L_Knee", "R_Knee", "L_Foot", "R_Foot"
            };
            for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
            {
                var record = runtimeState.AssembledParts[i];
                if (record?.instance == null || record.part == null ||
                    !record.part.UsesSynchronizedSkinnedRig)
                {
                    continue;
                }

                var skinnedRenderers = record.instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var added = new HashSet<Transform>();
                for (var rendererIndex = 0; rendererIndex < skinnedRenderers.Length; rendererIndex++)
                {
                    var bones = skinnedRenderers[rendererIndex].bones;
                    for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    {
                        var candidate = bones[boneIndex];
                        if (candidate != null && wanted.Contains(candidate.name) && added.Add(candidate))
                        {
                            poseProbes.Add(new PoseProbe(
                                $"{record.slot}/{candidate.name}",
                                candidate));
                        }
                    }
                }
            }

            Debug.Log($"[AC6CombatShowcase] Body pose probes: {poseProbes.Count}.", mech);
        }

        private void CapturePhasePoseStart()
        {
            phaseStartRotations.Clear();
            for (var i = 0; i < poseProbes.Count; i++)
            {
                var transform = poseProbes[i].Transform;
                if (transform != null)
                {
                    phaseStartRotations[transform] = transform.localRotation;
                }
            }
        }

        private void SamplePhasePose(ShowcasePhase phase)
        {
            var maxDelta = 0f;
            for (var i = 0; i < poseProbes.Count; i++)
            {
                var transform = poseProbes[i].Transform;
                if (transform != null && phaseStartRotations.TryGetValue(transform, out var startRotation))
                {
                    maxDelta = Mathf.Max(maxDelta, Quaternion.Angle(startRotation, transform.localRotation));
                }
            }

            if (!phasePoseDeltas.TryGetValue(phase, out var existing) || maxDelta > existing)
            {
                phasePoseDeltas[phase] = maxDelta;
            }
        }

        private void FinalizePhasePose()
        {
            if (activePhase < 0)
            {
                return;
            }

            var phase = (ShowcasePhase)activePhase;
            phasePoseDeltas.TryGetValue(phase, out var delta);
            Debug.Log(
                $"[AC6CombatShowcase] Body pose phase {phase}: maxLocalRotationDelta={delta:0.###}deg " +
                $"probes={poseProbes.Count}.",
                mech);
            if (phase == ShowcasePhase.ForwardAssaultBoost ||
                phase == ShowcasePhase.LeftQuickBoost ||
                phase == ShowcasePhase.RightQuickBoost ||
                phase == ShowcasePhase.RaiseAndFire ||
                phase == ShowcasePhase.Recovery)
            {
                var pass = poseProbes.Count > 0 && delta > 0.5f;
                Debug.Log(
                    $"[AC6CombatShowcase] Body pose check: {(pass ? "PASS" : "FAIL")} " +
                    $"phase={phase} delta={delta:0.###}deg.",
                    mech);
            }
        }

        private void SetBoosterExhaust(bool enabled, float intensity)
        {
            for (var i = 0; i < boosterExhausts.Count; i++)
            {
                var particles = boosterExhausts[i];
                var emission = particles.emission;
                emission.rateOverTime = 80f * Mathf.Max(0.1f, intensity);
                var main = particles.main;
                main.startSpeed = 10f * Mathf.Max(0.25f, intensity);
                if (enabled)
                {
                    if (!particles.isPlaying)
                    {
                        particles.Play();
                    }
                }
                else
                {
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }

        private void SpawnBurstAtBoosters(Color color, float size)
        {
            for (var i = 0; i < boosterExhausts.Count; i++)
            {
                SpawnBurst(boosterExhausts[i].transform.parent, color, size);
            }
        }

        private static void SpawnBurst(Transform anchor, Color color, float size)
        {
            if (anchor == null)
            {
                return;
            }

            var root = new GameObject("Showcase_Weapon_Burst");
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = Vector3.zero;
            // Same convention as CreateExhaust: post-fix dummies emit along their own +Z.
            root.transform.localRotation = Quaternion.identity;
            var particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.duration = 0.35f;
            main.loop = false;
            main.startLifetime = 0.32f;
            main.startSpeed = 6f;
            main.startSize = size;
            main.startColor = color;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 14f;
            shape.radius = 0.04f;
            root.GetComponent<ParticleSystemRenderer>().sharedMaterial = CreateRuntimeMaterial(
                "Showcase Burst",
                color,
                true);
            particles.Play();
            Destroy(root, 1.5f);
        }

        private void CountMaterialBindings()
        {
            materialCount = 0;
            materialsWithBaseMap = 0;
            var renderers = mech.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var materials = renderers[i].sharedMaterials;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    var material = materials[materialIndex];
                    if (material == null)
                    {
                        continue;
                    }

                    materialCount++;
                    if (material.mainTexture != null ||
                        (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null))
                    {
                        materialsWithBaseMap++;
                    }
                }
            }

            Debug.Log(
                $"[AC6CombatShowcase] Material bindings: {materialsWithBaseMap}/{materialCount} have base textures. " +
                "Zone paint masks/palette are not implemented yet.",
                mech);
        }

        private void OnGUI()
        {
            if (MechAssemblyMenu.IsAnyMenuVisible || integratedRadarHudPresent)
            {
                return;
            }

            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 16,
                normal = { textColor = Color.white }
            };
            GUI.Box(
                new Rect(18f, 18f, 560f, 132f),
                $"AC6 固定动作演示\n" +
                $"阶段：{PhaseDisplayName((ShowcasePhase)Mathf.Max(0, activePhase))}\n" +
                $"全身：{bodyActionPlayer?.ActiveFullBodyActionId ?? "-"}   " +
                $"上身：{bodyActionPlayer?.ActiveUpperBodyActionId ?? "-"}\n" +
                $"材质贴图：{materialsWithBaseMap}/{materialCount}\n" +
                "前向位移由运行时模拟；左右 QB 使用方向段 + 已目检制动段。",
                style);
        }

        private static string PhaseDisplayName(ShowcasePhase phase)
        {
            switch (phase)
            {
                case ShowcasePhase.Standby:
                    return "待机";
                case ShowcasePhase.ForwardAssaultBoost:
                    return "前向 AB（官方 Boost Flight 姿态）";
                case ShowcasePhase.LeftQuickBoost:
                    return "左 QB";
                case ShowcasePhase.RightQuickBoost:
                    return "右 QB";
                case ShowcasePhase.RaiseAndFire:
                    return "抬臂、保持瞄准、开火";
                default:
                    return "恢复待机";
            }
        }

        private static string FirstAvailableAction(
            MechEquipmentAnimationPlayer player,
            params string[] actionIds)
        {
            for (var i = 0; i < actionIds.Length; i++)
            {
                if (player.HasAction(actionIds[i]))
                {
                    return actionIds[i];
                }
            }

            return string.Empty;
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static Material CreateRuntimeMaterial(string materialName, Color color, bool emissive)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = materialName };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            if (emissive && material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 4f);
            }
            return material;
        }

        private readonly struct PoseProbe
        {
            public PoseProbe(string label, Transform transform)
            {
                Label = label;
                Transform = transform;
            }

            public string Label { get; }
            public Transform Transform { get; }
        }
    }

    [DisallowMultipleComponent]
    public sealed class AC6ShowcaseMissile : MonoBehaviour
    {
        private Transform target;
        private float speed;
        private float turnRate;
        private float delay;
        private float life;

        public void Configure(Transform newTarget, float newSpeed, float newTurnRate, float launchDelay)
        {
            target = newTarget;
            speed = newSpeed;
            turnRate = newTurnRate;
            delay = launchDelay;
        }

        private void Update()
        {
            life += Time.deltaTime;
            if (life > 8f)
            {
                Destroy(gameObject);
                return;
            }

            if (delay > 0f)
            {
                delay -= Time.deltaTime;
                return;
            }

            var desired = target == null
                ? transform.forward
                : (target.position - transform.position).normalized;
            if (desired.sqrMagnitude < 0.001f)
            {
                desired = transform.forward;
            }

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(desired, Vector3.up),
                1f - Mathf.Exp(-turnRate * Time.deltaTime));
            transform.position += transform.forward * (speed * Time.deltaTime);
        }
    }
}
