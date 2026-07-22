using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 物理/运动层（架构第 2 层）：消费输入快照，计算推力、速度与无滚转朝向。
    /// 不选择动画；动画根运动关闭，本组件是世界位移与 Yaw 的唯一权威。
    /// 太空机动原则：无空气阻力、有惯性、转身不等于改变速度方向——
    /// 作战速度包线只限制继续增速的推力分量，不回收已有动量；辅助飞控只用有限反推
    /// 修正侧向漂移。assist=0 时退化为纯牛顿推力积分。
    /// </summary>
    public sealed class MechFlightMotionController : MonoBehaviour
    {
        [Header("推进力模型")]
        [Tooltip("优先读取当前换装的总质量与推进器主推/侧推/反推数据。关闭后使用下方测试回退值。")]
        [SerializeField] private bool useLoadoutPropulsionStats = true;
        [SerializeField] private float fallbackMassKg = 50000f;
        [Tooltip("无有效换装推进数据时使用的背部主推力。")]
        [SerializeField] private float fallbackMainThrustKn = 2000f;
        [Tooltip("无有效换装推进数据时使用的单一侧向合力；左右能力相同。")]
        [SerializeField] private float fallbackLateralThrustKn = 1200f;
        [Tooltip("无有效换装推进数据时使用的正面反推力。")]
        [SerializeField] private float fallbackReverseThrustKn = 1400f;
        [Tooltip("上升喷口合力。当前零件表没有独立垂直推力字段，因此暂用显式参数。")]
        [SerializeField] private float verticalThrustKn = 1500f;
        [Tooltip("主动下降可调用的垂直机动推力，相对于上升喷口合力的比例。")]
        [Range(0f, 1f)] [SerializeField] private float descentThrustFraction = 0.45f;
        [Tooltip("垂直推力中由肩部上推喷口承担的比例；其余由双腿/脚部喷口承担。")]
        [Range(0f, 1f)] [SerializeField] private float shoulderLiftFraction = 0.4f;
        [Tooltip("开启矢量推力时，肩部上推喷口相对世界向上允许的最大偏转角。")]
        [Range(0f, 45f)] [SerializeField] private float shoulderLiftVectorDegrees = 12f;
        [Tooltip("空中常规平移时，肩部与腿部喷口参与协同机动的额定升力比例。")]
        [Range(0f, 1f)] [SerializeField] private float translationLiftAssistFraction = 0.35f;
        [Tooltip("空中常规前进、反推与侧移时，腿部姿态朝机动方向偏转的角度。")]
        [Range(0f, 75f)] [SerializeField] private float legTranslationDeflectionDegrees = 48f;
        [Tooltip("爬升或 AB 时，腿部姿态可以把脚部上推力偏向机动方向的最大角度。")]
        [Range(0f, 75f)] [SerializeField] private float legClimbDeflectionDegrees = 36f;
        [Tooltip("QB 时腿部姿态可以把脚部上推力偏向 QB 方向的最大角度。")]
        [Range(0f, 80f)] [SerializeField] private float legQuickBoostDeflectionDegrees = 52f;
        [Tooltip("主动刹车时腿部姿态可以把脚部上推力偏向反速度方向的最大角度。")]
        [Range(0f, 80f)] [SerializeField] private float legBrakeDeflectionDegrees = 48f;
        [Tooltip("某组喷口偏转损失垂直分量时，另一组可调用的额外补偿推力比例。")]
        [Range(0f, 1f)] [SerializeField] private float liftCompensationReserve = 0.35f;
        [Tooltip("开启后，背部主推和 AB 可在俯仰方向偏转；关闭后严格沿机体 +Z 纵轴。")]
        [SerializeField] private bool vectorThrustEnabled = true;
        [Tooltip("主推矢量喷口相对机体纵轴允许的最大俯仰偏角。")]
        [Range(0f, 89f)] [SerializeField] private float maximumVectorPitchDegrees = 35f;
        [Tooltip("相对于任务局部参考系的常规作战速度包线。它限制继续增速的推力，不截断已有速度。")]
        [SerializeField] private float operationalSpeedEnvelope = 250f;
        [Tooltip("开始逐渐削弱向外加速分量的速度区间宽度。")]
        [SerializeField] private float envelopeResponseBand = 40f;
        [Tooltip("仅用于防止 Unity 数值和碰撞失稳的极高技术安全上限，不属于玩法速度上限。")]
        [SerializeField] private float technicalSafetySpeed = 2000f;
        [Tooltip("辅助飞控强度 [0,1]：0=纯惯性；1=以最大辅助反推修正侧向漂移。")]
        [Range(0f, 1f)] [SerializeField] private float flightAssist = 0.6f;
        [Tooltip("辅助飞控可调用的最大修正推力。它仍然受机体质量影响。")]
        [SerializeField] private float flightAssistThrustKn = 600f;

        [Header("惯性推力锁定")]
        [Tooltip("允许按住输入锁定键冻结水平推力参考；松开后恢复机体朝向参考。")]
        [SerializeField] private bool inertialThrustLockEnabled = true;

        [Header("喷口分配与损坏补偿")]
        [Tooltip("单侧喷口损坏后，飞控允许健侧喷口继续贡献的比例。0 表示严格对称降额；1 表示完全补偿且不模拟姿态扰动。")]
        [Range(0f, 1f)] [SerializeField] private float damagedSideCompensationEfficiency = 0.65f;

        [Header("Quick Boost")]
        [Tooltip("QB 窗口内持续施加的固定推力。默认值对 50 吨机体产生约 320 m/s²。")]
        [SerializeField] private float quickBoostThrustKn = 16000f;
        [Tooltip("QB 持续施加推力的时间。")]
        [SerializeField] private float quickBoostDuration = 0.28f;

        [Header("Assault Boost")]
        [Tooltip("AB 在背部主推之外附加的固定推力。")]
        [SerializeField] private float assaultBoostThrustKn = 3500f;

        [Header("地面制动")]
        [Tooltip("接地且没有移动输入时的水平减速度。太空中不使用该阻力。")]
        [SerializeField] private float groundBrakingAcceleration = 90f;
        [SerializeField] private float groundStopSpeed = 0.15f;

        [Header("空中速度衰减")]
        [Tooltip("空中无推进输入时的微弱速度衰减。设为 0 即为严格无阻力惯性；应显著小于地面摩擦。")]
        [SerializeField] private float airCoastingDragAcceleration = 0.35f;

        [Header("主动制动")]
        [Tooltip("X 制动调用对应方向喷口时的推力倍率。1 表示不超出正常主推/侧推/反推能力。")]
        [SerializeField] private float activeBrakingThrustMultiplier = 1f;

        [Header("朝向")]
        [Tooltip("鼠标水平输入转换为目标 Yaw 的灵敏度（度/像素）。实际转向由 RCS 与动量轮扭矩完成。")]
        [SerializeField] private float yawSensitivity = 0.15f;
        [Tooltip("模型视觉前方相对逻辑前方(+Z)的 Yaw 修正（度）。校准模型轴用，禁止用镜头摆位掩盖。")]
        [SerializeField] private float modelForwardYawOffset;

        [Header("姿态控制")]
        [Tooltip("肩部左右 RCS 的总额定推力。它们构成主要偏航力偶，不产生净平移力。")]
        [SerializeField] private float shoulderRcsThrustKn = 650f;
        [Tooltip("腿部左右 RCS 的总额定推力。它们作为次级偏航力偶和损坏冗余。")]
        [SerializeField] private float legRcsThrustKn = 250f;
        [Tooltip("核心动量轮最大偏航扭矩，只负责精调、止转和 RCS 死区。")]
        [SerializeField] private float momentumWheelTorqueKnM = 120f;
        [Tooltip("飞控允许的最大偏航角速度。")]
        [SerializeField] private float maximumYawRateDegrees = 180f;
        [Tooltip("偏航闭环达到目标的响应时间。越短越接近直接鼠标转向。")]
        [Range(0.06f, 0.5f)] [SerializeField] private float yawResponseTimeSeconds = 0.16f;
        [Tooltip("偏航闭环阻尼比。大于 1 可抑制重型机体越过目标后反复摆动。")]
        [Range(0.7f, 2f)] [SerializeField] private float yawDampingRatio = 1.15f;
        [Tooltip("制动时由核心动量轮主动承担的目标扭矩比例，其余由 RCS 力偶承担。")]
        [Range(0f, 1f)] [SerializeField] private float momentumWheelBrakingShare = 0.35f;
        [Tooltip("估算惯量过小时使用的下限，防止不完整测试模型瞬间转向。")]
        [SerializeField] private float minimumYawInertiaKgM2 = 25000f;

        [Header("接地")]
        [SerializeField] private float groundY;
        [SerializeField] private float groundedEpsilon = 0.05f;
        [Tooltip("向下探测测试场地和可站立碰撞面的最大距离。")]
        [SerializeField] private float groundProbeDistance = 512f;
        [Tooltip("可站立表面的最小向上法线分量；0.45 约等于允许 63 度以内的坡。")]
        [Range(0.05f, 1f)] [SerializeField] private float minimumGroundNormalY = 0.45f;
        [Tooltip("接地移动时允许贴住的最大向下落差。超过该值视为驶离坡顶，保留离坡速度进入空中。")]
        [SerializeField] private float maximumGroundSnapDown = 0.45f;
        [Tooltip("接地移动时单帧允许爬上的最大高度，防止射线把机体吸到高处平台。")]
        [SerializeField] private float maximumGroundSnapUp = 1.5f;
        [Tooltip("非接地时的重力加速度。设为 0 可切换为纯深空惯性。")]
        [SerializeField] private float gravityAcceleration = 9.81f;

        private float yawDegrees;
        private float commandedYawDegrees;
        private float yawAngularVelocityRadians;
        private float yawMomentOfInertiaKgM2;
        private float attitudeWidthMeters = 1f;
        private float attitudeDepthMeters = 1f;
        private float shoulderRcsLeverArmMeters = 1f;
        private float legRcsLeverArmMeters = 0.6f;
        private float currentYawTorqueNewtonMeters;
        private float currentShoulderRcsTorqueNewtonMeters;
        private float currentLegRcsTorqueNewtonMeters;
        private float currentMomentumWheelTorqueNewtonMeters;
        private float aimPitchDegrees;
        private Vector3 velocity;
        private float quickBoostRemaining;
        private Vector3 quickBoostDirection;
        private bool assaultBoostActive;
        private bool braking;
        private float mainThrottle;
        private bool flightEnvelopeLimiting;
        private bool flightEnvelopeOverrideActive;
        private bool inertialThrustLockActive;
        private Vector3 inertialThrustLockForward = Vector3.forward;
        private float brakingAltitude;
        private float sampledGroundY;
        private Vector3 sampledGroundNormal = Vector3.up;
        private bool isGrounded;
        private Rigidbody massBody;
        private MechRuntimeState runtimeState;
        private float currentMassKg;
        private Vector3 currentControlForceNewtons;
        private Vector3 currentControlAcceleration;
        private Vector3 currentShoulderLiftForceNewtons;
        private Vector3 currentLegLiftForceNewtons;
        private Vector3 currentQuickBoostForceNewtons;
        private Vector3 fixedControlCenterLocal;
        private float quickBoostForceNewtons;
        private readonly RaycastHit[] groundHits = new RaycastHit[32];
        private readonly List<MechThrusterChannelState> thrusterChannels = new();

        public float YawDegrees => yawDegrees;

        public float LogicalYawDegrees => yawDegrees;

        public float CommandedYawDegrees => commandedYawDegrees;

        public float YawRateDegreesPerSecond => yawAngularVelocityRadians * Mathf.Rad2Deg;

        public float CurrentYawTorqueNewtonMeters => currentYawTorqueNewtonMeters;

        public float CurrentShoulderRcsTorqueNewtonMeters => currentShoulderRcsTorqueNewtonMeters;

        public float CurrentLegRcsTorqueNewtonMeters => currentLegRcsTorqueNewtonMeters;

        public float CurrentMomentumWheelTorqueNewtonMeters => currentMomentumWheelTorqueNewtonMeters;

        public Vector3 Velocity => velocity;

        public float HorizontalSpeed => new Vector2(velocity.x, velocity.z).magnitude;

        public float MainThrottle => mainThrottle;

        public float OperationalSpeedEnvelope => operationalSpeedEnvelope;

        public float TechnicalSafetySpeed => technicalSafetySpeed;

        public float GravityAcceleration => gravityAcceleration;

        public float CurrentMassKg => ResolveMassKg();

        public Vector3 CurrentControlForceNewtons => currentControlForceNewtons;

        /// <summary>肩部上推喷口本帧施加到机体上的世界空间力。</summary>
        public Vector3 CurrentShoulderLiftForceNewtons => currentShoulderLiftForceNewtons;

        /// <summary>脚部向下排气产生的本帧世界空间上推力；方向由腿部机动姿态决定。</summary>
        public Vector3 CurrentLegLiftForceNewtons => currentLegLiftForceNewtons;

        public Vector3 CurrentQuickBoostForceNewtons => currentQuickBoostForceNewtons;

        public Vector3 FixedControlCenterWorld => transform.TransformPoint(fixedControlCenterLocal);

        public IReadOnlyList<MechThrusterChannelState> ThrusterChannels => thrusterChannels;

        public bool VectorThrustEnabled => vectorThrustEnabled;

        /// <summary>当前帧经速度包线处理后的持续推进加速度，不含重力与短时 QB。</summary>
        public Vector3 CurrentControlAcceleration => currentControlAcceleration;

        /// <summary>当前 QB 窗口的世界空间加速度；窗口结束后返回零。</summary>
        public Vector3 CurrentQuickBoostAcceleration => currentQuickBoostForceNewtons / ResolveMassKg();

        public float CurrentQuickBoostRemaining => quickBoostRemaining;

        public float AirCoastingDragAcceleration => airCoastingDragAcceleration;

        public bool FlightEnvelopeLimiting => flightEnvelopeLimiting;

        public bool FlightEnvelopeOverrideActive => flightEnvelopeOverrideActive;

        public bool InertialThrustLockActive => inertialThrustLockActive;

        public Vector3 InertialThrustLockForward => inertialThrustLockForward;

        public float Altitude
        {
            get
            {
                RefreshGroundHeight(transform.position);
                return Mathf.Max(0f, transform.position.y - sampledGroundY);
            }
        }

        public bool Braking => braking;

        public bool QuickBoostActive => quickBoostRemaining > 0f;

        public bool Grounded
        {
            get => isGrounded;
        }

        /// <summary>AB 可由输入触发的短时推进或后续行为图状态共同维持。</summary>
        public bool AssaultBoostActive
        {
            get => assaultBoostActive;
            set => SetAssaultBoostActive(value);
        }

        /// <summary>逻辑朝向（无滚转）。模型轴修正只应用到可见根节点，不污染移动与镜头朝向。</summary>
        public Quaternion LogicalRotation => Quaternion.Euler(0f, LogicalYawDegrees, 0f);

        public float ModelForwardYawOffset => modelForwardYawOffset;

        private void Awake()
        {
            massBody = GetComponent<Rigidbody>();
            runtimeState = GetComponent<MechRuntimeState>();
            yawDegrees = Mathf.Repeat(transform.eulerAngles.y - modelForwardYawOffset, 360f);
            commandedYawDegrees = yawDegrees;
            sampledGroundY = groundY;
            RefreshGroundHeight(transform.position);
            isGrounded = transform.position.y <= sampledGroundY + groundedEpsilon;
        }

        private void Start()
        {
            RefreshThrusterLayout();
        }

        public void RefreshThrusterLayout()
        {
            var previousHealth = new Dictionary<string, float>(StringComparer.Ordinal);
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                previousHealth[thrusterChannels[i].Id] = thrusterChannels[i].Health;
            }

            thrusterChannels.Clear();

            var coreBounds = TryGetPartBounds(MechPartSlot.Core, out var resolvedCore)
                ? resolvedCore
                : ResolveAllPartBounds();
            var legBounds = TryGetPartBounds(MechPartSlot.Legs, out var resolvedLegs)
                ? resolvedLegs
                : coreBounds;
            fixedControlCenterLocal = transform.InverseTransformPoint(coreBounds.center);
            var allPartBounds = ResolveAllPartBounds();
            attitudeWidthMeters = Mathf.Max(0.5f, allPartBounds.size.x);
            attitudeDepthMeters = Mathf.Max(0.5f, allPartBounds.size.z);

            var logicalRight = LogicalRotation * Vector3.right;
            var logicalForward = LogicalRotation * Vector3.forward;
            var coreHalfWidth = Mathf.Max(0.6f, coreBounds.extents.x * 0.65f);
            var coreHalfDepth = Mathf.Max(0.45f, coreBounds.extents.z * 0.7f);
            var shoulderCenter = coreBounds.center + Vector3.up * (coreBounds.extents.y * 0.25f);
            var legCenter = legBounds.center - Vector3.up * (legBounds.extents.y * 0.55f);
            var legHalfWidth = Mathf.Max(0.35f, legBounds.extents.x * 0.28f);
            shoulderRcsLeverArmMeters = coreHalfWidth;
            legRcsLeverArmMeters = legHalfWidth;

            ResolveMainNozzleAnchors(
                coreBounds,
                logicalRight,
                logicalForward,
                coreHalfWidth,
                coreHalfDepth,
                out var leftMainAnchor,
                out var rightMainAnchor,
                out var leftMainFallback,
                out var rightMainFallback);

            AddThrusterChannel(
                "main.left", MechThrusterRole.Main, MechThrusterSide.Left,
                leftMainAnchor, leftMainFallback, leftMainAnchor == null, previousHealth);
            AddThrusterChannel(
                "main.right", MechThrusterRole.Main, MechThrusterSide.Right,
                rightMainAnchor, rightMainFallback, rightMainAnchor == null, previousHealth);

            var reverseCenter = coreBounds.center + logicalForward * coreHalfDepth;
            AddProvisionalPair(
                "reverse", MechThrusterRole.Reverse,
                reverseCenter, logicalRight, coreHalfWidth * 0.55f, previousHealth);

            AddThrusterChannel(
                "strafe.positive", MechThrusterRole.StrafePositive, MechThrusterSide.Left,
                null,
                transform.InverseTransformPoint(coreBounds.center - logicalRight * coreHalfWidth),
                true,
                previousHealth);
            AddThrusterChannel(
                "strafe.negative", MechThrusterRole.StrafeNegative, MechThrusterSide.Right,
                null,
                transform.InverseTransformPoint(coreBounds.center + logicalRight * coreHalfWidth),
                true,
                previousHealth);

            AddProvisionalPair(
                "shoulder", MechThrusterRole.ShoulderLift,
                shoulderCenter, logicalRight, coreHalfWidth, previousHealth);
            AddProvisionalPair(
                "leg", MechThrusterRole.LegLift,
                legCenter, logicalRight, legHalfWidth, previousHealth);
            AddProvisionalPair(
                "rcs.shoulder", MechThrusterRole.ShoulderRcs,
                shoulderCenter, logicalRight, coreHalfWidth, previousHealth);
            AddProvisionalPair(
                "rcs.leg", MechThrusterRole.LegRcs,
                legCenter, logicalRight, legHalfWidth, previousHealth);
        }

        public bool SetThrusterHealth(string channelId, float health)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return false;
            }

            EnsureThrusterLayout();
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (!string.Equals(thrusterChannels[i].Id, channelId, StringComparison.Ordinal))
                {
                    continue;
                }

                thrusterChannels[i].SetHealth(health);
                return true;
            }

            return false;
        }

        public void RestoreAllThrusters()
        {
            EnsureThrusterLayout();
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                thrusterChannels[i].SetHealth(1f);
            }
        }

        public void ConfigureModelForwardYawOffset(float yawOffsetDegrees)
        {
            modelForwardYawOffset = Mathf.Repeat(yawOffsetDegrees, 360f);
            yawDegrees = 0f;
            commandedYawDegrees = yawDegrees;
            yawAngularVelocityRadians = 0f;
            ApplyVisualRotation();
        }

        public void SetAimPitch(float pitchDegrees)
        {
            aimPitchDegrees = Mathf.Clamp(pitchDegrees, -89f, 89f);
        }

        public void TriggerAssaultBoost()
        {
            SetAssaultBoostActive(true);
        }

        public bool ToggleAssaultBoost()
        {
            SetAssaultBoostActive(!assaultBoostActive);
            return assaultBoostActive;
        }

        public void SetAssaultBoostActive(bool active)
        {
            assaultBoostActive = active;
            if (active)
            {
                quickBoostRemaining = 0f;
            }
        }

        public void ConfigureFlightEnvelope(
            float operationalEnvelope,
            float safetySpeed,
            float responseBand)
        {
            operationalSpeedEnvelope = Mathf.Max(0f, operationalEnvelope);
            technicalSafetySpeed = Mathf.Max(operationalSpeedEnvelope, safetySpeed);
            envelopeResponseBand = Mathf.Clamp(
                responseBand, 0f, operationalSpeedEnvelope);
        }

        public void ConfigureQuickBoost(float targetVelocityChange, float duration)
        {
            quickBoostThrustKn = duration > Mathf.Epsilon
                ? Mathf.Max(0f, targetVelocityChange) * ResolveMassKg() / duration / 1000f
                : 0f;
            quickBoostDuration = Mathf.Max(0f, duration);
        }

        public void ConfigurePropulsionForceModel(
            bool useLoadoutStats,
            bool enableVectorThrust,
            float vectorPitchLimitDegrees)
        {
            useLoadoutPropulsionStats = useLoadoutStats;
            vectorThrustEnabled = enableVectorThrust;
            maximumVectorPitchDegrees = Mathf.Clamp(vectorPitchLimitDegrees, 0f, 89f);
        }

        public void ConfigureTranslationThrusterAssist(
            float assistFraction,
            float translationDeflectionDegrees,
            float climbDeflectionDegrees,
            float quickBoostDeflectionDegrees,
            float brakeDeflectionDegrees)
        {
            translationLiftAssistFraction = Mathf.Clamp01(assistFraction);
            legTranslationDeflectionDegrees = Mathf.Clamp(translationDeflectionDegrees, 0f, 75f);
            legClimbDeflectionDegrees = Mathf.Clamp(climbDeflectionDegrees, 0f, 75f);
            legQuickBoostDeflectionDegrees = Mathf.Clamp(quickBoostDeflectionDegrees, 0f, 80f);
            legBrakeDeflectionDegrees = Mathf.Clamp(brakeDeflectionDegrees, 0f, 80f);
        }

        public void ConfigureAttitudeControl(
            float shoulderRcsKn,
            float legRcsKn,
            float wheelTorqueKnM,
            float maximumYawRate)
        {
            shoulderRcsThrustKn = Mathf.Max(0f, shoulderRcsKn);
            legRcsThrustKn = Mathf.Max(0f, legRcsKn);
            momentumWheelTorqueKnM = Mathf.Max(0f, wheelTorqueKnM);
            maximumYawRateDegrees = Mathf.Max(0f, maximumYawRate);
        }

        public void SetVectorThrustEnabled(bool enabled)
        {
            vectorThrustEnabled = enabled;
        }

        public void ConfigureGravity(float acceleration)
        {
            gravityAcceleration = Mathf.Max(0f, acceleration);
        }

        public void Tick(in MechPlayerInputFrame frame, float deltaTime)
        {
            EnsureThrusterLayout();
            ClearThrusterForces();
            currentMassKg = ResolveMassKg();
            currentControlForceNewtons = Vector3.zero;
            currentControlAcceleration = Vector3.zero;
            currentShoulderLiftForceNewtons = Vector3.zero;
            currentLegLiftForceNewtons = Vector3.zero;
            currentQuickBoostForceNewtons = Vector3.zero;

            // 鼠标只改变目标 Yaw。实际 Yaw 由肩/腿 RCS 力偶和核心动量轮积分得到；Roll 恒 0。
            // Keep yaw as a direct, roll-free control while the physical RCS model is deferred.
            // This restores the original mouse response and prevents angular inertia overshoot.
            yawDegrees = Mathf.Repeat(
                yawDegrees + frame.MouseDelta.x * yawSensitivity,
                360f);
            commandedYawDegrees = yawDegrees;
            yawAngularVelocityRadians = 0f;
            currentYawTorqueNewtonMeters = 0f;
            currentShoulderRcsTorqueNewtonMeters = 0f;
            currentLegRcsTorqueNewtonMeters = 0f;
            currentMomentumWheelTorqueNewtonMeters = 0f;

            if (frame.BrakeHeld && !braking)
            {
                RefreshGroundHeight(transform.position);
                brakingAltitude = Mathf.Max(transform.position.y, sampledGroundY);
            }

            braking = frame.BrakeHeld;
            if (braking)
            {
                // 主动制动优先于 AB 和普通推进。它只消除水平速度，Y 轴由高度保持接管。
                quickBoostRemaining = 0f;
            }

            var quickBoostStep = !braking
                ? Mathf.Min(Mathf.Max(0f, deltaTime), quickBoostRemaining)
                : 0f;
            if (quickBoostRemaining > 0f)
            {
                quickBoostRemaining = Mathf.Max(0f, quickBoostRemaining - deltaTime);
            }

            var grounded = isGrounded;
            var yawBasis = LogicalRotation;
            var right = yawBasis * Vector3.right;
            var bodyForward = yawBasis * Vector3.forward;
            var mainThrustDirection = ResolveMainThrustDirection(yawBasis, grounded);
            UpdateInertialThrustLock(in frame, bodyForward);
            var controlForward = inertialThrustLockActive
                ? inertialThrustLockForward
                : bodyForward;
            var controlRight = inertialThrustLockActive
                ? Vector3.Cross(Vector3.up, controlForward).normalized
                : right;
            mainThrottle = Mathf.Clamp01(frame.MainThrottle);

            // 节流是持久的前向推力命令。W/S 在该命令上临时加减，A/D 仍是侧向推进。
            var forwardCommand = Mathf.Clamp(frame.MoveLocal.y + mainThrottle, -1f, 1f);
            ResolveThrusterForces(
                out var mainThrustNewtons,
                out var lateralThrustNewtons,
                out var reverseThrustNewtons);

            // 每一组喷口独立贡献力。斜向输入会同时点燃主推和侧推，不再把合力归一化。
            var propulsionForce = Vector3.zero;
            if (!braking && inertialThrustLockActive)
            {
                var lockedRequest = controlRight * (frame.MoveLocal.x * lateralThrustNewtons);
                lockedRequest += controlForward * (forwardCommand >= 0f
                    ? forwardCommand * mainThrustNewtons
                    : forwardCommand * reverseThrustNewtons);
                propulsionForce += AllocateDirectionPreservingForce(
                    lockedRequest,
                    yawBasis,
                    mainThrustNewtons,
                    lateralThrustNewtons,
                    reverseThrustNewtons);
            }
            else if (!braking && Mathf.Abs(frame.MoveLocal.x) > Mathf.Epsilon)
            {
                var strafeRole = frame.MoveLocal.x > 0f
                    ? MechThrusterRole.StrafePositive
                    : MechThrusterRole.StrafeNegative;
                propulsionForce += AllocateThrusterForce(
                    strafeRole,
                    right * (frame.MoveLocal.x * lateralThrustNewtons),
                    lateralThrustNewtons,
                    false);
            }

            if (!braking && !inertialThrustLockActive && forwardCommand >= 0f)
            {
                var mainRequest = forwardCommand * mainThrustNewtons;
                if (AssaultBoostActive)
                {
                    mainRequest += assaultBoostThrustKn * 1000f;
                }

                propulsionForce += AllocateThrusterForce(
                    MechThrusterRole.Main,
                    mainThrustDirection * mainRequest,
                    mainThrustNewtons + (AssaultBoostActive ? assaultBoostThrustKn * 1000f : 0f),
                    true);
            }
            else if (!braking && !inertialThrustLockActive)
            {
                propulsionForce += AllocateThrusterForce(
                    MechThrusterRole.Reverse,
                    -bodyForward * (-forwardCommand * reverseThrustNewtons),
                    reverseThrustNewtons,
                    true);
            }

            var quickBoostApplying = quickBoostStep > 0f;
            ResolveLiftThrusterForces(
                in frame,
                yawBasis,
                mainThrustDirection,
                controlRight,
                controlForward,
                grounded,
                quickBoostApplying,
                out var requestedShoulderLiftForce,
                out var requestedLegLiftForce);
            var shoulderCapacity = verticalThrustKn * 1000f
                                   * Mathf.Clamp01(shoulderLiftFraction)
                                   * (1f + liftCompensationReserve);
            var legCapacity = verticalThrustKn * 1000f
                              * (1f - Mathf.Clamp01(shoulderLiftFraction))
                              * (1f + liftCompensationReserve);
            currentShoulderLiftForceNewtons = AllocateThrusterForce(
                MechThrusterRole.ShoulderLift,
                requestedShoulderLiftForce,
                shoulderCapacity,
                true);
            currentLegLiftForceNewtons = AllocateThrusterForce(
                MechThrusterRole.LegLift,
                requestedLegLiftForce,
                legCapacity,
                true);
            propulsionForce += currentShoulderLiftForceNewtons + currentLegLiftForceNewtons;

            var hasPropulsionCommand = propulsionForce.sqrMagnitude > 0.0001f;
            flightEnvelopeOverrideActive = frame.FlightEnvelopeOverrideHeld
                                           || AssaultBoostActive
                                           || quickBoostApplying;
            flightEnvelopeLimiting = false;

            if (!braking && hasPropulsionCommand)
            {
                propulsionForce = ApplyFlightEnvelopeToThrusterForces(
                    propulsionForce, flightEnvelopeOverrideActive);
                currentShoulderLiftForceNewtons = SumThrusterForce(MechThrusterRole.ShoulderLift);
                currentLegLiftForceNewtons = SumThrusterForce(MechThrusterRole.LegLift);
                var propulsionAcceleration = propulsionForce / currentMassKg;
                currentControlForceNewtons = propulsionForce;
                currentControlAcceleration = propulsionAcceleration;
                velocity += propulsionAcceleration * deltaTime;

                // 辅助飞控只用有限反推消除相对命令方向的横向漂移；不再 Lerp 到目标速度。
                var commandDirection = propulsionAcceleration.sqrMagnitude > 0.0001f
                    ? propulsionAcceleration.normalized
                    : propulsionForce.normalized;
                if (commandDirection.sqrMagnitude > 0.0001f && flightAssist > 0f)
                {
                    var lateralVelocity = velocity
                                          - Vector3.Project(velocity, commandDirection);
                    var assistAcceleration = flightAssistThrustKn * 1000f
                                             / currentMassKg * flightAssist;
                    var requestedAssistDeltaVelocity = Vector3.ClampMagnitude(
                        -lateralVelocity,
                        assistAcceleration * deltaTime);
                    if (deltaTime > Mathf.Epsilon)
                    {
                        var requestedAssistForce = requestedAssistDeltaVelocity
                                                   * (currentMassKg / deltaTime);
                        var assistForce = AllocateDirectionalReserveForce(
                            requestedAssistForce,
                            yawBasis);
                        velocity += assistForce / currentMassKg * deltaTime;
                        currentControlForceNewtons += assistForce;
                        currentControlAcceleration = currentControlForceNewtons / currentMassKg;
                    }
                }
            }

            if (quickBoostApplying && quickBoostDirection.sqrMagnitude > 0.0001f)
            {
                currentQuickBoostForceNewtons = AllocateDirectionalReserveForce(
                    quickBoostDirection * quickBoostForceNewtons,
                    yawBasis);
                velocity += currentQuickBoostForceNewtons
                            / currentMassKg * quickBoostStep;
            }

            if (!braking && !grounded)
            {
                velocity.y -= gravityAcceleration * deltaTime;
            }

            if (braking)
            {
                var horizontal = new Vector3(velocity.x, 0f, velocity.z);
                var brakingDirection = horizontal.sqrMagnitude > 0.0001f
                    ? -horizontal.normalized
                    : Vector3.zero;
                var brakingForceCapacity = ResolveDirectionalForceCapacity(
                    brakingDirection,
                    yawBasis,
                    mainThrustNewtons,
                    lateralThrustNewtons,
                    reverseThrustNewtons);
                var horizontalBrakingForce = AllocateDirectionalReserveForce(
                    brakingDirection * brakingForceCapacity * activeBrakingThrustMultiplier,
                    yawBasis);
                var allocatedLiftForce = currentShoulderLiftForceNewtons
                                         + currentLegLiftForceNewtons;
                var totalBrakingForce = horizontalBrakingForce + allocatedLiftForce;
                var brakingAcceleration = Mathf.Max(
                    0f,
                    Vector3.Dot(totalBrakingForce, brakingDirection) / currentMassKg);
                horizontal = Vector3.MoveTowards(
                    horizontal,
                    Vector3.zero,
                    brakingAcceleration * deltaTime);
                if (horizontal.magnitude <= groundStopSpeed)
                {
                    horizontal = Vector3.zero;
                }

                velocity = new Vector3(horizontal.x, 0f, horizontal.z);
                currentControlForceNewtons = totalBrakingForce;
                currentControlAcceleration = currentControlForceNewtons / currentMassKg;
            }

            // 无输入时空中保留惯性；地面则由接触摩擦可靠退出 QB/滑行。
            if (!braking && grounded && !hasPropulsionCommand &&
                !AssaultBoostActive && !quickBoostApplying)
            {
                var horizontal = new Vector3(velocity.x, 0f, velocity.z);
                horizontal = Vector3.MoveTowards(
                    horizontal, Vector3.zero, groundBrakingAcceleration * deltaTime);
                if (horizontal.magnitude <= groundStopSpeed)
                {
                    horizontal = Vector3.zero;
                }

                velocity.x = horizontal.x;
                velocity.z = horizontal.z;
            }
            else if (!braking && !grounded && !frame.RiseHeld &&
                     !hasPropulsionCommand && !AssaultBoostActive &&
                     !quickBoostApplying)
            {
                velocity = Vector3.MoveTowards(
                    velocity,
                    Vector3.zero,
                    airCoastingDragAcceleration * deltaTime);
            }

            // 该限制只防止引擎/碰撞把 Unity 推入数值失稳，不代表世界观内的速度上限。
            if (technicalSafetySpeed > 0f &&
                velocity.sqrMagnitude > technicalSafetySpeed * technicalSafetySpeed)
            {
                velocity = Vector3.ClampMagnitude(velocity, technicalSafetySpeed);
            }

            var previousPosition = transform.position;
            var position = previousPosition + velocity * deltaTime;
            if (braking)
            {
                position.y = brakingAltitude;
                velocity.y = 0f;
            }

            var surfaceY = SampleGroundHeight(position, out var surfaceNormal);
            if (braking)
            {
                isGrounded = position.y <= surfaceY + groundedEpsilon;
            }
            else if (grounded && !frame.RiseHeld)
            {
                var surfaceDelta = surfaceY - previousPosition.y;
                if (surfaceDelta >= -maximumGroundSnapDown &&
                    surfaceDelta <= maximumGroundSnapUp)
                {
                    // Follow a nearby surface, and preserve its tangent as real velocity.
                    // At a ramp edge the sampled floor drops beyond maximumGroundSnapDown,
                    // so this vertical component survives and launches the mech naturally.
                    position.y = surfaceY;
                    velocity.y = deltaTime > Mathf.Epsilon
                        ? surfaceDelta / deltaTime
                        : 0f;
                    isGrounded = true;
                }
                else
                {
                    isGrounded = false;
                }
            }
            else if (!braking && !frame.RiseHeld && velocity.y <= 0f && position.y <= surfaceY)
            {
                position.y = surfaceY;
                velocity.y = 0f;
                isGrounded = true;
            }
            else if (!braking)
            {
                isGrounded = false;
            }

            sampledGroundY = surfaceY;
            sampledGroundNormal = surfaceNormal;

            transform.SetPositionAndRotation(
                position, Quaternion.Euler(0f, yawDegrees + modelForwardYawOffset, 0f));
        }

        private void EnsureThrusterLayout()
        {
            if (thrusterChannels.Count == 0)
            {
                RefreshThrusterLayout();
            }
        }

        private void ClearThrusterForces()
        {
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                thrusterChannels[i].ClearForce();
            }
        }

        private void UpdateAttitudeControl(float deltaTime)
        {
            currentYawTorqueNewtonMeters = 0f;
            currentShoulderRcsTorqueNewtonMeters = 0f;
            currentLegRcsTorqueNewtonMeters = 0f;
            currentMomentumWheelTorqueNewtonMeters = 0f;
            if (deltaTime <= Mathf.Epsilon)
            {
                return;
            }

            yawMomentOfInertiaKgM2 = Mathf.Max(
                minimumYawInertiaKgM2,
                currentMassKg
                * (attitudeWidthMeters * attitudeWidthMeters
                   + attitudeDepthMeters * attitudeDepthMeters)
                / 12f);

            var errorRadians = Mathf.DeltaAngle(yawDegrees, commandedYawDegrees) * Mathf.Deg2Rad;
            var maximumRate = Mathf.Max(0f, maximumYawRateDegrees) * Mathf.Deg2Rad;
            var shoulderCapacity = ResolveYawRcsTorqueCapacity(
                MechThrusterRole.ShoulderRcs,
                shoulderRcsThrustKn * 1000f,
                shoulderRcsLeverArmMeters);
            var legCapacity = ResolveYawRcsTorqueCapacity(
                MechThrusterRole.LegRcs,
                legRcsThrustKn * 1000f,
                legRcsLeverArmMeters);
            var wheelCapacity = Mathf.Max(0f, momentumWheelTorqueKnM) * 1000f;
            var totalCapacity = shoulderCapacity + legCapacity + wheelCapacity;
            if (totalCapacity <= Mathf.Epsilon)
            {
                yawAngularVelocityRadians = 0f;
                return;
            }

            // A slightly over-damped PD loop keeps the high-output RCS responsive without
            // allowing the body to coast through the commanded heading.
            var responseTime = Mathf.Max(0.06f, yawResponseTimeSeconds);
            var naturalFrequency = 4f / responseTime;
            var desiredAngularAcceleration = naturalFrequency * naturalFrequency * errorRadians
                                             - 2f * Mathf.Max(0.7f, yawDampingRatio)
                                             * naturalFrequency * yawAngularVelocityRadians;
            var requestedTorque = Mathf.Clamp(
                desiredAngularAcceleration * yawMomentOfInertiaKgM2,
                -totalCapacity,
                totalCapacity);

            // 小修正优先由动量轮完成；超过其能力后，肩部 RCS 先于腿部 RCS 接管。
            currentMomentumWheelTorqueNewtonMeters = Mathf.Clamp(
                requestedTorque,
                -wheelCapacity,
                wheelCapacity);
            var isBraking = Mathf.Abs(yawAngularVelocityRadians) > 0.002f
                            && requestedTorque * yawAngularVelocityRadians < 0f;
            currentMomentumWheelTorqueNewtonMeters = isBraking
                ? Mathf.Clamp(
                    requestedTorque * Mathf.Clamp01(momentumWheelBrakingShare),
                    -wheelCapacity,
                    wheelCapacity)
                : 0f;
            var remainingTorque = requestedTorque - currentMomentumWheelTorqueNewtonMeters;
            currentShoulderRcsTorqueNewtonMeters = AllocateYawRcsTorque(
                MechThrusterRole.ShoulderRcs,
                remainingTorque,
                shoulderRcsThrustKn * 1000f,
                shoulderRcsLeverArmMeters);
            remainingTorque -= currentShoulderRcsTorqueNewtonMeters;
            currentLegRcsTorqueNewtonMeters = AllocateYawRcsTorque(
                MechThrusterRole.LegRcs,
                remainingTorque,
                legRcsThrustKn * 1000f,
                legRcsLeverArmMeters);
            remainingTorque -= currentLegRcsTorqueNewtonMeters;
            var remainingWheelCapacity = Mathf.Max(
                0f,
                wheelCapacity - Mathf.Abs(currentMomentumWheelTorqueNewtonMeters));
            currentMomentumWheelTorqueNewtonMeters += Mathf.Clamp(
                remainingTorque,
                -remainingWheelCapacity,
                remainingWheelCapacity);
            currentYawTorqueNewtonMeters = currentMomentumWheelTorqueNewtonMeters
                                           + currentShoulderRcsTorqueNewtonMeters
                                           + currentLegRcsTorqueNewtonMeters;

            yawAngularVelocityRadians += currentYawTorqueNewtonMeters
                                         / yawMomentOfInertiaKgM2 * deltaTime;
            yawAngularVelocityRadians = Mathf.Clamp(
                yawAngularVelocityRadians,
                -maximumRate,
                maximumRate);
            var yawStepDegrees = yawAngularVelocityRadians * Mathf.Rad2Deg * deltaTime;
            var remainingErrorDegrees = Mathf.DeltaAngle(yawDegrees, commandedYawDegrees);
            var crossesTarget = Mathf.Abs(yawStepDegrees) >= Mathf.Abs(remainingErrorDegrees)
                                && Mathf.Sign(yawStepDegrees) == Mathf.Sign(remainingErrorDegrees);
            if (crossesTarget)
            {
                yawDegrees = commandedYawDegrees;
                yawAngularVelocityRadians = 0f;
            }
            else
            {
                yawDegrees = Mathf.Repeat(yawDegrees + yawStepDegrees, 360f);
            }

            if (Mathf.Abs(errorRadians) < 0.0002f &&
                Mathf.Abs(yawAngularVelocityRadians) < 0.002f)
            {
                yawDegrees = commandedYawDegrees;
                yawAngularVelocityRadians = 0f;
            }
        }

        private float ResolveYawRcsTorqueCapacity(
            MechThrusterRole role,
            float ratedGroupForceNewtons,
            float leverArmMeters)
        {
            var leftHealth = 0f;
            var rightHealth = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role)
                {
                    continue;
                }

                if (channel.Side == MechThrusterSide.Left) leftHealth += channel.Health;
                if (channel.Side == MechThrusterSide.Right) rightHealth += channel.Health;
            }

            var pairedHealth = Mathf.Clamp01(Mathf.Min(leftHealth, rightHealth));
            return Mathf.Max(0f, ratedGroupForceNewtons)
                   * pairedHealth
                   * Mathf.Max(0.05f, leverArmMeters);
        }

        private float AllocateYawRcsTorque(
            MechThrusterRole role,
            float requestedTorqueNewtonMeters,
            float ratedGroupForceNewtons,
            float leverArmMeters)
        {
            var capacity = ResolveYawRcsTorqueCapacity(
                role,
                ratedGroupForceNewtons,
                leverArmMeters);
            var appliedTorque = Mathf.Clamp(
                requestedTorqueNewtonMeters,
                -capacity,
                capacity);
            if (Mathf.Abs(appliedTorque) <= Mathf.Epsilon)
            {
                return 0f;
            }

            var controlCenter = FixedControlCenterWorld;
            var usableLeverArmSum = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role || !channel.Operational)
                {
                    continue;
                }

                var radial = channel.ResolveWorldOrigin(transform) - controlCenter;
                radial.y = 0f;
                usableLeverArmSum += radial.magnitude;
            }

            if (usableLeverArmSum <= Mathf.Epsilon)
            {
                return 0f;
            }

            var forceMagnitude = Mathf.Abs(appliedTorque) / usableLeverArmSum;
            var torqueSign = Mathf.Sign(appliedTorque);
            var realizedTorque = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role || !channel.Operational)
                {
                    continue;
                }

                var radial = channel.ResolveWorldOrigin(transform) - controlCenter;
                radial.y = 0f;
                if (radial.sqrMagnitude <= Mathf.Epsilon)
                {
                    continue;
                }

                // F = up x r guarantees a positive Unity yaw torque from r x F.
                var forceDirection = Vector3.Cross(Vector3.up, radial).normalized * torqueSign;
                var force = forceDirection * forceMagnitude;
                channel.AddForce(force);
                realizedTorque += Vector3.Dot(Vector3.Cross(radial, force), Vector3.up);
            }

            return realizedTorque;
        }

        private Vector3 AllocateThrusterForce(
            MechThrusterRole role,
            Vector3 requestedForce,
            float ratedGroupForceNewtons,
            bool compensatePairedSides)
        {
            var requestedMagnitude = requestedForce.magnitude;
            var ratedMagnitude = Mathf.Max(0f, ratedGroupForceNewtons);
            if (requestedMagnitude <= Mathf.Epsilon || ratedMagnitude <= Mathf.Epsilon)
            {
                return Vector3.zero;
            }

            var channelCount = 0;
            var leftCount = 0;
            var rightCount = 0;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (thrusterChannels[i].Role != role)
                {
                    continue;
                }

                channelCount++;
                if (thrusterChannels[i].Side == MechThrusterSide.Left) leftCount++;
                if (thrusterChannels[i].Side == MechThrusterSide.Right) rightCount++;
            }

            if (channelCount == 0)
            {
                return requestedForce;
            }

            var leftCapacity = 0f;
            var rightCapacity = 0f;
            var centerCapacity = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role)
                {
                    continue;
                }

                float nominalCapacity;
                switch (channel.Side)
                {
                    case MechThrusterSide.Left when leftCount > 0 && rightCount > 0:
                        nominalCapacity = ratedMagnitude * 0.5f / leftCount;
                        leftCapacity += nominalCapacity * channel.Health;
                        break;
                    case MechThrusterSide.Right when leftCount > 0 && rightCount > 0:
                        nominalCapacity = ratedMagnitude * 0.5f / rightCount;
                        rightCapacity += nominalCapacity * channel.Health;
                        break;
                    default:
                        nominalCapacity = ratedMagnitude / channelCount;
                        centerCapacity += nominalCapacity * channel.Health;
                        break;
                }
            }

            var allowedLeftCapacity = leftCapacity;
            var allowedRightCapacity = rightCapacity;
            var effectiveCapacity = leftCapacity + rightCapacity + centerCapacity;
            if (compensatePairedSides && leftCount > 0 && rightCount > 0)
            {
                var symmetricSideCapacity = Mathf.Min(leftCapacity, rightCapacity);
                allowedLeftCapacity = symmetricSideCapacity
                                      + Mathf.Max(0f, leftCapacity - rightCapacity)
                                      * damagedSideCompensationEfficiency;
                allowedRightCapacity = symmetricSideCapacity
                                       + Mathf.Max(0f, rightCapacity - leftCapacity)
                                       * damagedSideCompensationEfficiency;
                effectiveCapacity = allowedLeftCapacity + allowedRightCapacity + centerCapacity;
            }

            var appliedMagnitude = Mathf.Min(requestedMagnitude, effectiveCapacity);
            if (appliedMagnitude <= Mathf.Epsilon || effectiveCapacity <= Mathf.Epsilon)
            {
                return Vector3.zero;
            }

            var direction = requestedForce / requestedMagnitude;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role || !channel.Operational)
                {
                    continue;
                }

                float nominalCapacity;
                switch (channel.Side)
                {
                    case MechThrusterSide.Left when leftCount > 0 && rightCount > 0:
                        nominalCapacity = ratedMagnitude * 0.5f / leftCount;
                        break;
                    case MechThrusterSide.Right when leftCount > 0 && rightCount > 0:
                        nominalCapacity = ratedMagnitude * 0.5f / rightCount;
                        break;
                    default:
                        nominalCapacity = ratedMagnitude / channelCount;
                        break;
                }

                var channelCapacity = nominalCapacity * channel.Health;
                var allowedSideCapacity = centerCapacity;
                var rawSideCapacity = centerCapacity;
                if (channel.Side == MechThrusterSide.Left && leftCount > 0 && rightCount > 0)
                {
                    allowedSideCapacity = allowedLeftCapacity;
                    rawSideCapacity = leftCapacity;
                }
                else if (channel.Side == MechThrusterSide.Right && leftCount > 0 && rightCount > 0)
                {
                    allowedSideCapacity = allowedRightCapacity;
                    rawSideCapacity = rightCapacity;
                }

                if (rawSideCapacity <= Mathf.Epsilon)
                {
                    continue;
                }

                var channelAllowedCapacity = channelCapacity
                                             * (allowedSideCapacity / rawSideCapacity);
                channel.AddForce(
                    direction * (appliedMagnitude * channelAllowedCapacity / effectiveCapacity));
            }

            return direction * appliedMagnitude;
        }

        private Vector3 AllocateDirectionalReserveForce(
            Vector3 requestedForce,
            Quaternion yawBasis)
        {
            if (requestedForce.sqrMagnitude <= Mathf.Epsilon)
            {
                return Vector3.zero;
            }

            // QB、制动和飞控修正也必须走实体喷口。将命令拆到机体轴，避免用一组
            // 喷口伪造任意方向的合力；斜向修正会真实同时调用主推/反推与侧推。
            var localForce = Quaternion.Inverse(yawBasis) * requestedForce;
            var allocatedForce = Vector3.zero;
            if (Mathf.Abs(localForce.z) > Mathf.Epsilon)
            {
                var longitudinalForce = yawBasis * new Vector3(0f, 0f, localForce.z);
                allocatedForce += AllocateThrusterForce(
                    localForce.z >= 0f
                        ? MechThrusterRole.Main
                        : MechThrusterRole.Reverse,
                    longitudinalForce,
                    Mathf.Abs(localForce.z),
                    true);
            }

            if (Mathf.Abs(localForce.x) > Mathf.Epsilon)
            {
                var lateralForce = yawBasis * new Vector3(localForce.x, 0f, 0f);
                allocatedForce += AllocateThrusterForce(
                    localForce.x >= 0f
                        ? MechThrusterRole.StrafePositive
                        : MechThrusterRole.StrafeNegative,
                    lateralForce,
                    Mathf.Abs(localForce.x),
                    false);
            }

            // 当前喷口表只有向上的升力通道。向下修正必须等待对应实体喷口完成映射，
            // 不能临时让肩部或脚部喷口反向喷射。
            if (localForce.y > Mathf.Epsilon)
            {
                var shoulderFraction = Mathf.Clamp01(shoulderLiftFraction);
                var shoulderRequest = Vector3.up * (localForce.y * shoulderFraction);
                var legRequest = Vector3.up * (localForce.y * (1f - shoulderFraction));
                allocatedForce += AllocateThrusterForce(
                    MechThrusterRole.ShoulderLift,
                    shoulderRequest,
                    shoulderRequest.magnitude,
                    true);
                allocatedForce += AllocateThrusterForce(
                    MechThrusterRole.LegLift,
                    legRequest,
                    legRequest.magnitude,
                    true);
            }

            return allocatedForce;
        }

        private Vector3 AllocateDirectionPreservingForce(
            Vector3 requestedForce,
            Quaternion yawBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons)
        {
            if (requestedForce.sqrMagnitude <= Mathf.Epsilon)
            {
                return Vector3.zero;
            }

            var direction = requestedForce.normalized;
            var capacity = ResolveDirectionPreservingForceCapacity(
                direction,
                yawBasis,
                mainNewtons,
                lateralNewtons,
                reverseNewtons);
            return AllocateDirectionalReserveForce(
                direction * Mathf.Min(requestedForce.magnitude, capacity),
                yawBasis);
        }

        private void UpdateInertialThrustLock(
            in MechPlayerInputFrame frame,
            Vector3 bodyForward)
        {
            // AB 保持原本的机体主推语义；结束后若 F 仍按住，会从当时机头重新捕获。
            var requested = inertialThrustLockEnabled
                            && frame.InertialThrustLockHeld
                            && !AssaultBoostActive;
            if (!requested)
            {
                inertialThrustLockActive = false;
                return;
            }

            if (inertialThrustLockActive)
            {
                return;
            }

            var horizontalForward = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
            inertialThrustLockForward = horizontalForward.sqrMagnitude > 0.0001f
                ? horizontalForward.normalized
                : Vector3.forward;
            inertialThrustLockActive = true;
        }

        private Vector3 ApplyFlightEnvelopeToThrusterForces(
            Vector3 totalForce,
            bool bypass)
        {
            if (bypass || operationalSpeedEnvelope <= 0f ||
                totalForce.sqrMagnitude <= 0.0001f || velocity.sqrMagnitude <= 0.0001f)
            {
                return totalForce;
            }

            var speed = velocity.magnitude;
            var responseStart = Mathf.Max(0f, operationalSpeedEnvelope - envelopeResponseBand);
            if (speed <= responseStart)
            {
                return totalForce;
            }

            var velocityDirection = velocity / speed;
            if (Vector3.Dot(totalForce, velocityDirection) <= 0f)
            {
                return totalForce;
            }

            var outwardScale = speed >= operationalSpeedEnvelope
                ? 0f
                : Mathf.InverseLerp(operationalSpeedEnvelope, responseStart, speed);
            flightEnvelopeLimiting = outwardScale < 0.999f;

            var limitedTotal = Vector3.zero;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (IsAttitudeThrusterRole(thrusterChannels[i].Role))
                {
                    continue;
                }

                var channelForce = thrusterChannels[i].CurrentForceNewtons;
                var outwardForce = Vector3.Dot(channelForce, velocityDirection);
                if (outwardForce > 0f)
                {
                    channelForce -= velocityDirection * outwardForce * (1f - outwardScale);
                    thrusterChannels[i].SetForce(channelForce);
                }

                limitedTotal += channelForce;
            }

            return limitedTotal;
        }

        private static bool IsAttitudeThrusterRole(MechThrusterRole role)
        {
            return role == MechThrusterRole.ShoulderRcs || role == MechThrusterRole.LegRcs;
        }

        private Vector3 SumThrusterForce(MechThrusterRole role)
        {
            var force = Vector3.zero;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (thrusterChannels[i].Role == role)
                {
                    force += thrusterChannels[i].CurrentForceNewtons;
                }
            }

            return force;
        }

        private void AddProvisionalPair(
            string idPrefix,
            MechThrusterRole role,
            Vector3 worldCenter,
            Vector3 worldRight,
            float halfWidth,
            IReadOnlyDictionary<string, float> previousHealth)
        {
            AddThrusterChannel(
                idPrefix + ".left",
                role,
                MechThrusterSide.Left,
                null,
                transform.InverseTransformPoint(worldCenter - worldRight * halfWidth),
                true,
                previousHealth);
            AddThrusterChannel(
                idPrefix + ".right",
                role,
                MechThrusterSide.Right,
                null,
                transform.InverseTransformPoint(worldCenter + worldRight * halfWidth),
                true,
                previousHealth);
        }

        private void AddThrusterChannel(
            string id,
            MechThrusterRole role,
            MechThrusterSide side,
            Transform anchor,
            Vector3 fallbackLocalPosition,
            bool provisionalOrigin,
            IReadOnlyDictionary<string, float> previousHealth)
        {
            var channel = new MechThrusterChannelState(
                id,
                role,
                side,
                anchor,
                fallbackLocalPosition,
                provisionalOrigin);
            if (previousHealth.TryGetValue(id, out var savedHealth))
            {
                channel.SetHealth(savedHealth);
            }

            thrusterChannels.Add(channel);
        }

        private void ResolveMainNozzleAnchors(
            Bounds coreBounds,
            Vector3 logicalRight,
            Vector3 logicalForward,
            float halfWidth,
            float halfDepth,
            out Transform leftAnchor,
            out Transform rightAnchor,
            out Vector3 leftFallbackLocal,
            out Vector3 rightFallbackLocal)
        {
            var fallbackCenter = coreBounds.center - logicalForward * halfDepth;
            leftFallbackLocal = transform.InverseTransformPoint(
                fallbackCenter - logicalRight * halfWidth * 0.55f);
            rightFallbackLocal = transform.InverseTransformPoint(
                fallbackCenter + logicalRight * halfWidth * 0.55f);
            leftAnchor = null;
            rightAnchor = null;

            var parts = GetComponentsInChildren<MechAssembledPart>(true);
            var leftProjection = float.PositiveInfinity;
            var rightProjection = float.NegativeInfinity;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Slot != MechPartSlot.Booster)
                {
                    continue;
                }

                var candidates = parts[i].GetComponentsInChildren<Transform>(true);
                for (var j = 0; j < candidates.Length; j++)
                {
                    var candidate = candidates[j];
                    if (!candidate.name.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var projection = Vector3.Dot(
                        candidate.position - coreBounds.center,
                        logicalRight);
                    if (projection < leftProjection)
                    {
                        leftProjection = projection;
                        leftAnchor = candidate;
                    }

                    if (projection > rightProjection)
                    {
                        rightProjection = projection;
                        rightAnchor = candidate;
                    }
                }
            }

            if (leftAnchor == rightAnchor)
            {
                if (leftAnchor != null && leftProjection <= 0f)
                {
                    rightAnchor = null;
                }
                else
                {
                    leftAnchor = null;
                }
            }
        }

        private bool TryGetPartBounds(MechPartSlot slot, out Bounds bounds)
        {
            var parts = GetComponentsInChildren<MechAssembledPart>(true);
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Slot == slot && TryGetRendererBounds(parts[i].gameObject, out bounds))
                {
                    return true;
                }
            }

            bounds = default;
            return false;
        }

        private Bounds ResolveAllPartBounds()
        {
            var parts = GetComponentsInChildren<MechAssembledPart>(true);
            var initialized = false;
            var bounds = default(Bounds);
            for (var i = 0; i < parts.Length; i++)
            {
                if (!TryGetRendererBounds(parts[i].gameObject, out var partBounds))
                {
                    continue;
                }

                if (!initialized)
                {
                    bounds = partBounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(partBounds);
                }
            }

            return initialized
                ? bounds
                : new Bounds(transform.position + Vector3.up * 2f, new Vector3(4f, 8f, 4f));
        }

        private static bool TryGetRendererBounds(GameObject root, out Bounds bounds)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var initialized = false;
            bounds = default;
            for (var i = 0; i < renderers.Length; i++)
            {
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

        private float ResolveMassKg()
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var loadoutMass = runtimeState != null && runtimeState.AggregatedStats != null
                ? runtimeState.AggregatedStats.totalMassKg
                : 0f;
            if (loadoutMass > 1f)
            {
                return loadoutMass;
            }

            if (massBody == null)
            {
                massBody = GetComponent<Rigidbody>();
            }

            return massBody != null && massBody.mass > 1f
                ? massBody.mass
                : Mathf.Max(1f, fallbackMassKg);
        }

        private void ResolveThrusterForces(
            out float mainNewtons,
            out float lateralNewtons,
            out float reverseNewtons)
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var stats = useLoadoutPropulsionStats && runtimeState != null
                ? runtimeState.AggregatedStats
                : null;
            mainNewtons = (stats != null && stats.mainThrustKn > 0f
                ? stats.mainThrustKn
                : fallbackMainThrustKn) * 1000f;
            lateralNewtons = (stats != null && stats.lateralThrustKn > 0f
                ? stats.lateralThrustKn
                : fallbackLateralThrustKn) * 1000f;
            reverseNewtons = (stats != null && stats.reverseThrustKn > 0f
                ? stats.reverseThrustKn
                : fallbackReverseThrustKn) * 1000f;
        }

        private Vector3 ResolveMainThrustDirection(Quaternion yawBasis, bool grounded)
        {
            if (grounded || !vectorThrustEnabled || maximumVectorPitchDegrees <= 0f)
            {
                return yawBasis * Vector3.forward;
            }

            var vectorPitch = Mathf.Clamp(
                aimPitchDegrees,
                -maximumVectorPitchDegrees,
                maximumVectorPitchDegrees);
            return Quaternion.Euler(vectorPitch, LogicalYawDegrees, 0f) * Vector3.forward;
        }

        private void ResolveLiftThrusterForces(
            in MechPlayerInputFrame frame,
            Quaternion yawBasis,
            Vector3 mainThrustDirection,
            Vector3 controlRight,
            Vector3 controlForward,
            bool grounded,
            bool quickBoostApplying,
            out Vector3 shoulderForce,
            out Vector3 legForce)
        {
            shoulderForce = Vector3.zero;
            legForce = Vector3.zero;

            var totalLiftNewtons = Mathf.Max(0f, verticalThrustKn) * 1000f;
            if (totalLiftNewtons <= Mathf.Epsilon)
            {
                return;
            }

            // Ctrl 下降是主动向下施力，不直接改写高度或垂直速度。
            // Space 与 Ctrl 同时按下时互相抵消，避免相反的喷口组同时满推。
            var verticalInput = (frame.RiseHeld ? 1f : 0f) - (frame.DescendHeld ? 1f : 0f);
            if (verticalInput < -Mathf.Epsilon)
            {
                var descentNewtons = totalLiftNewtons
                                     * Mathf.Clamp01(descentThrustFraction)
                                     * -verticalInput;
                var descentShoulderFraction = Mathf.Clamp01(shoulderLiftFraction);
                shoulderForce = Vector3.down * (descentNewtons * descentShoulderFraction);
                legForce = Vector3.down * (descentNewtons * (1f - descentShoulderFraction));
                return;
            }

            // 按住上升键时允许全额爬升。空中 QB、AB 与主动刹车至少调用足以抵消
            // 当前微重力的推力，避免动作姿态把垂直支撑凭空丢掉。
            var horizontalIntent = ResolveHorizontalManeuverIntent(
                in frame,
                yawBasis,
                mainThrustDirection,
                controlRight,
                controlForward,
                quickBoostApplying);
            var longitudinalCommand = Mathf.Clamp(frame.MoveLocal.y + mainThrottle, -1f, 1f);
            var translationCommandMagnitude = Mathf.Clamp01(
                new Vector2(frame.MoveLocal.x, longitudinalCommand).magnitude);

            var liftCommand = Mathf.Max(0f, verticalInput);
            if (!grounded && (quickBoostApplying || AssaultBoostActive || braking))
            {
                var gravityCompensation = currentMassKg * gravityAcceleration / totalLiftNewtons;
                liftCommand = Mathf.Max(liftCommand, Mathf.Clamp01(gravityCompensation));
            }

            // In free flight, normal translation is a coordinated burn: the main/reverse/strafe
            // group supplies the primary horizontal force while shoulder and leg groups add a
            // bounded vectored contribution. Their upward component remains physical.
            if (!grounded && vectorThrustEnabled && !quickBoostApplying &&
                !AssaultBoostActive && !braking && translationCommandMagnitude > Mathf.Epsilon)
            {
                liftCommand = Mathf.Max(
                    liftCommand,
                    translationCommandMagnitude * Mathf.Clamp01(translationLiftAssistFraction));
            }

            if (liftCommand <= Mathf.Epsilon)
            {
                return;
            }

            var shoulderDirection = Vector3.up;
            if (vectorThrustEnabled && horizontalIntent.sqrMagnitude > 0.0001f)
            {
                shoulderDirection = TiltUpToward(
                    horizontalIntent, shoulderLiftVectorDegrees);
            }

            var legDeflection = 0f;
            if (braking)
            {
                legDeflection = legBrakeDeflectionDegrees;
            }
            else if (quickBoostApplying)
            {
                legDeflection = legQuickBoostDeflectionDegrees;
            }
            else if (frame.RiseHeld || AssaultBoostActive)
            {
                legDeflection = legClimbDeflectionDegrees;
            }
            else if (horizontalIntent.sqrMagnitude > 0.0001f)
            {
                legDeflection = legTranslationDeflectionDegrees;
            }

            // 脚部喷口自身不做矢量偏转；这里的方向变化代表整条腿/脚在对应动作中转向。
            var legDirection = horizontalIntent.sqrMagnitude > 0.0001f && legDeflection > 0f
                ? TiltUpToward(horizontalIntent, legDeflection)
                : Vector3.up;

            var shoulderFraction = Mathf.Clamp01(shoulderLiftFraction);
            var shoulderBaseMagnitude = totalLiftNewtons * shoulderFraction * liftCommand;
            var legBaseMagnitude = totalLiftNewtons * (1f - shoulderFraction) * liftCommand;
            shoulderForce = shoulderDirection * shoulderBaseMagnitude;
            legForce = legDirection * legBaseMagnitude;

            // 偏转会损失垂直分量。先由肩部储备补腿部，再由腿部储备补肩部；
            // 仍受显式储备上限约束，不制造无限悬停力。
            var desiredVerticalForce = totalLiftNewtons * liftCommand;
            var verticalDeficit = Mathf.Max(
                0f,
                desiredVerticalForce - shoulderForce.y - legForce.y);
            verticalDeficit = AddVerticalCompensation(
                ref shoulderForce,
                shoulderDirection,
                shoulderBaseMagnitude * liftCompensationReserve,
                verticalDeficit);
            AddVerticalCompensation(
                ref legForce,
                legDirection,
                legBaseMagnitude * liftCompensationReserve,
                verticalDeficit);
        }

        private Vector3 ResolveHorizontalManeuverIntent(
            in MechPlayerInputFrame frame,
            Quaternion yawBasis,
            Vector3 mainThrustDirection,
            Vector3 controlRight,
            Vector3 controlForward,
            bool quickBoostApplying)
        {
            if (braking)
            {
                var horizontalVelocity = Vector3.ProjectOnPlane(velocity, Vector3.up);
                return horizontalVelocity.sqrMagnitude > 0.0001f
                    ? -horizontalVelocity.normalized
                    : Vector3.zero;
            }

            if (quickBoostApplying && quickBoostDirection.sqrMagnitude > 0.0001f)
            {
                return Vector3.ProjectOnPlane(quickBoostDirection, Vector3.up).normalized;
            }

            if (AssaultBoostActive)
            {
                return Vector3.ProjectOnPlane(mainThrustDirection, Vector3.up).normalized;
            }

            var longitudinalCommand = Mathf.Clamp(frame.MoveLocal.y + mainThrottle, -1f, 1f);
            var inputDirection = controlRight * frame.MoveLocal.x
                                 + controlForward * longitudinalCommand;
            return inputDirection.sqrMagnitude > 0.0001f
                ? inputDirection.normalized
                : Vector3.zero;
        }

        private static Vector3 TiltUpToward(Vector3 horizontalDirection, float degrees)
        {
            var horizontal = Vector3.ProjectOnPlane(horizontalDirection, Vector3.up);
            if (horizontal.sqrMagnitude <= 0.0001f || degrees <= 0f)
            {
                return Vector3.up;
            }

            horizontal.Normalize();
            var axis = Vector3.Cross(Vector3.up, horizontal);
            return (Quaternion.AngleAxis(Mathf.Clamp(degrees, 0f, 89f), axis) * Vector3.up)
                .normalized;
        }

        private static float AddVerticalCompensation(
            ref Vector3 force,
            Vector3 direction,
            float reserveMagnitude,
            float verticalDeficit)
        {
            if (verticalDeficit <= Mathf.Epsilon || reserveMagnitude <= Mathf.Epsilon ||
                direction.y <= 0.0001f)
            {
                return verticalDeficit;
            }

            var addedMagnitude = Mathf.Min(reserveMagnitude, verticalDeficit / direction.y);
            force += direction * addedMagnitude;
            return Mathf.Max(0f, verticalDeficit - addedMagnitude * direction.y);
        }

        private static float ResolveDirectionalForceCapacity(
            Vector3 worldDirection,
            Quaternion yawBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons)
        {
            if (worldDirection.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            var localDirection = Quaternion.Inverse(yawBasis) * worldDirection.normalized;
            var longitudinal = localDirection.z >= 0f
                ? localDirection.z * mainNewtons
                : -localDirection.z * reverseNewtons;
            var lateral = Mathf.Abs(localDirection.x) * lateralNewtons;
            return Mathf.Max(0f, longitudinal + lateral);
        }

        private static float ResolveDirectionPreservingForceCapacity(
            Vector3 worldDirection,
            Quaternion yawBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons)
        {
            if (worldDirection.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            var localDirection = Quaternion.Inverse(yawBasis) * worldDirection.normalized;
            var capacity = float.PositiveInfinity;
            if (Mathf.Abs(localDirection.z) > 0.0001f)
            {
                var longitudinalCapacity = localDirection.z >= 0f
                    ? mainNewtons
                    : reverseNewtons;
                capacity = Mathf.Min(
                    capacity,
                    longitudinalCapacity / Mathf.Abs(localDirection.z));
            }

            if (Mathf.Abs(localDirection.x) > 0.0001f)
            {
                capacity = Mathf.Min(
                    capacity,
                    lateralNewtons / Mathf.Abs(localDirection.x));
            }

            return float.IsPositiveInfinity(capacity) ? 0f : Mathf.Max(0f, capacity);
        }

        private void ApplyVisualRotation()
        {
            transform.localRotation = Quaternion.Euler(0f, yawDegrees + modelForwardYawOffset, 0f);
        }

        private void RefreshGroundHeight(Vector3 position)
        {
            sampledGroundY = SampleGroundHeight(position, out sampledGroundNormal);
        }

        private float SampleGroundHeight(Vector3 position)
        {
            return SampleGroundHeight(position, out _);
        }

        private float SampleGroundHeight(Vector3 position, out Vector3 surfaceNormal)
        {
            var origin = position + Vector3.up * 12f;
            var hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                groundHits,
                groundProbeDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            var highest = groundY;
            surfaceNormal = Vector3.up;
            for (var i = 0; i < hitCount; i++)
            {
                var hit = groundHits[i];
                if (hit.collider == null || hit.normal.y < minimumGroundNormalY)
                {
                    continue;
                }

                var hitTransform = hit.collider.transform;
                if (hitTransform == transform || hitTransform.IsChildOf(transform))
                {
                    continue;
                }

                if (hit.point.y > highest)
                {
                    highest = hit.point.y;
                    surfaceNormal = hit.normal;
                }
            }

            return highest;
        }

        /// <summary>启动固定方向的 QB 高加速度窗口，与 QB 动作状态同步触发。</summary>
        public void BeginQuickBoost(Vector2 moveLocal)
        {
            quickBoostRemaining = quickBoostDuration;
            var yawBasis = LogicalRotation;
            var forward = ResolveMainThrustDirection(yawBasis, Grounded);
            var direction = moveLocal.sqrMagnitude > 0.0001f
                ? yawBasis * Vector3.right * moveLocal.x + forward * moveLocal.y
                : forward;
            quickBoostDirection = direction.sqrMagnitude > 0.0001f
                ? direction.normalized
                : forward.normalized;

            ResolveThrusterForces(
                out var mainNewtons,
                out var lateralNewtons,
                out var reverseNewtons);
            var directionalCapacity = ResolveDirectionalForceCapacity(
                quickBoostDirection,
                yawBasis,
                mainNewtons,
                lateralNewtons,
                reverseNewtons);
            var directionalScale = mainNewtons > Mathf.Epsilon
                ? directionalCapacity / mainNewtons
                : 1f;
            quickBoostForceNewtons = quickBoostThrustKn * 1000f
                                     * Mathf.Max(0.05f, directionalScale);
        }
    }
}
