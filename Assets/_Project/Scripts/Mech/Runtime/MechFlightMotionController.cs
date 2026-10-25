using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 物理/运动层（架构第 2 层）：消费输入快照，计算推力、速度与无滚转朝向。
    /// 不选择动画；动画根运动关闭，本组件是世界位移与 Yaw 的唯一权威。
    /// 太空机动原则：无空气阻力、有惯性、转身不等于改变速度方向——
    /// 各方向推力独立叠加速度；只有主动制动或明确的轨迹同步命令可以消除已有速度。
    /// </summary>
    public sealed class MechFlightMotionController : MonoBehaviour
    {
        private const float DefaultManeuverEnergyCapacity = 100f;
        private const float DefaultMovementEnergyDrainPerSecond = 4f;
        private const float DefaultAssaultBoostEnergyDrainPerSecond = 15f;
        private const float DefaultQuickBoostEnergyCost = 18f;

        [Header("推进力模型")]
        [Tooltip("优先读取当前换装的总质量与推进器主推/侧推/反推数据。关闭后使用下方测试回退值。")]
        [SerializeField] private bool useLoadoutPropulsionStats = true;
        [SerializeField] private float fallbackMassKg = 50000f;
        [Tooltip("无有效换装推进数据时使用的背部主推力。")]
        [SerializeField] private float fallbackMainThrustKn = 4000f;
        [Tooltip("无有效换装推进数据时使用的单一侧向合力；左右能力相同。")]
        [SerializeField] private float fallbackLateralThrustKn = 2400f;
        [Tooltip("无有效换装推进数据时使用的正面反推力。")]
        [SerializeField] private float fallbackReverseThrustKn = 2800f;
        [Tooltip("旧装配未提供上推力数据时使用的兼容值。")]
        [SerializeField] private float verticalThrustKn = 3000f;
        [Tooltip("旧装配未提供下推力数据时，相对于兼容上推力的比例。")]
        [Range(0f, 1f)] [SerializeField] private float descentThrustFraction = 0.3f;
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
        [Tooltip("开启后，背部两台主推进器可在俯仰和偏航方向同步矢量偏转；关闭后严格沿机体 +Z 纵轴。")]
        [SerializeField] private bool vectorThrustEnabled = true;
        [Tooltip("主推矢量喷口相对机体纵轴允许的最大俯仰偏角。")]
        [Range(0f, 89f)] [SerializeField] private float maximumVectorPitchDegrees = 35f;
        [Tooltip("主推矢量喷口相对机体纵轴允许的最大左右偏角。")]
        [Range(0f, 60f)] [SerializeField] private float maximumVectorYawDegrees = 20f;
        [Tooltip("主推矢量机构每秒允许改变的最大角度。")]
        [Min(1f)] [SerializeField] private float mainVectorSlewRateDegreesPerSecond = 90f;
        [Tooltip("仅用于防止 Unity 数值和碰撞失稳的极高技术安全上限，不属于玩法速度上限。")]
        [SerializeField] private float technicalSafetySpeed = 0f;
        [Header("喷口分配与损坏补偿")]
        [Tooltip("单侧喷口损坏后，飞控允许健侧喷口继续贡献的比例。0 表示严格对称降额；1 表示完全补偿且不模拟姿态扰动。")]
        [Range(0f, 1f)] [SerializeField] private float damagedSideCompensationEfficiency = 0.65f;
        [Tooltip("除背部主推进器外，每个实体喷口的默认额定推力。喷口方向完全取自绑定节点的 -forward。")]
        [Min(0f)] [SerializeField] private float auxiliaryNozzleThrustKn = 600f;
        [Tooltip("三轴平移喷口分配器的迭代次数。喷口数量较少时 12 次足以稳定收敛。")]
        [Range(4, 32)] [SerializeField] private int translationAllocatorIterations = 12;
        [Tooltip("喷口分配时对绕质心残余力矩的抑制强度。推力误差始终是首要目标。")]
        [Range(0f, 0.5f)] [SerializeField] private float translationTorqueNeutralityWeight = 0.03f;

        [Header("Quick Boost")]
        [Tooltip("QB 窗口内持续施加的固定推力。默认值对 50 吨机体产生约 320 m/s²。")]
        [SerializeField] private float quickBoostThrustKn = 32000f;
        [Tooltip("QB 持续施加推力的时间。")]
        [SerializeField] private float quickBoostDuration = 0.28f;

        [Header("Assault Boost")]
        [Tooltip("AB 在背部主推之外附加的固定推力。")]
        [SerializeField] private float assaultBoostThrustKn = 7000f;

        [Header("机动能量")]
        [Tooltip("供普通推进、制动、轨道同步、AB 与 QB 共用的储能容量（MJ）。有效装配中由发电机决定。")]
        [SerializeField, Min(1f)] private float maximumManeuverEnergy =
            DefaultManeuverEnergyCapacity;
        [Tooltip("兼容旧配置：仅在 AB 激活且缺少部件数据时参与功耗下限计算。普通推进不消耗 EN。")]
        [SerializeField, Min(0f)] private float movementEnergyDrainPerSecond =
            DefaultMovementEnergyDrainPerSecond;
        [Tooltip("没有有效主推进器功耗数据时，AB 每秒消耗的回退能量（MJ/s）。")]
        [SerializeField, Min(0f)] private float assaultBoostEnergyDrainPerSecond =
            DefaultAssaultBoostEnergyDrainPerSecond;
        [Tooltip("没有有效主推进器功耗数据时，每次 QB 消耗的回退能量（MJ）。")]
        [SerializeField, Min(0f)] private float quickBoostEnergyCost =
            DefaultQuickBoostEnergyCost;
        [Tooltip("没有有效发电机数据时，每秒恢复的回退能量（MJ/s）。")]
        [SerializeField, Min(0f)] private float maneuverEnergyRechargePerSecond = 24f;
        [Tooltip("最后一次消耗后开始恢复前的等待时间。")]
        [SerializeField, Min(0f)] private float maneuverEnergyRechargeDelay = 0.75f;
        [Tooltip("能量耗尽后，至少恢复到该比例才重新允许普通推进。")]
        [SerializeField, Range(0f, 1f)] private float maneuverEnergyRestartFraction = 0.2f;

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
        [Tooltip("模型视觉前方相对逻辑前方(+Z)的 Yaw 修正（度）。校准模型轴用，禁止用镜头摆位掩盖。")]
        [SerializeField] private float modelForwardYawOffset;

        [Header("姿态控制")]
        [Tooltip("肩部左右 RCS 的总额定推力。它们构成主要偏航力偶，不产生净平移力。")]
        [SerializeField] private float shoulderRcsThrustKn = 1300f;
        [Tooltip("腿部左右 RCS 的总额定推力。它们作为次级偏航力偶和损坏冗余。")]
        [SerializeField] private float legRcsThrustKn = 500f;
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
        [SerializeField] private float groundedEpsilon = 0.05f;
        [Tooltip("向下探测测试场地和可站立碰撞面的最大距离。")]
        [SerializeField] private float groundProbeDistance = 512f;
        [Tooltip("可站立表面的最小向上法线分量；0.45 约等于允许 63 度以内的坡。")]
        [Range(0.05f, 1f)] [SerializeField] private float minimumGroundNormalY = 0.45f;
        [Tooltip("接地移动时允许贴住的最大向下落差。超过该值视为驶离坡顶，保留离坡速度进入空中。")]
        [SerializeField] private float maximumGroundSnapDown = 0.45f;
        [Tooltip("接地移动时单帧允许爬上的最大高度，防止射线把机体吸到高处平台。")]
        [SerializeField] private float maximumGroundSnapUp = 1.5f;
        [Tooltip("接地滑行时允许的最大仰角，防止保持接地状态绕过天顶。")]
        [Range(0f, 89f)] [SerializeField] private float maximumGroundedClimbPitchDegrees = 85f;
        [Tooltip("接地滑行时允许的最大俯角。0 表示机体前向不能指入地面。")]
        [Range(0f, 30f)] [SerializeField] private float maximumGroundedDivePitchDegrees;
        [Tooltip("非接地时的重力加速度。设为 0 可切换为纯深空惯性。")]
        [SerializeField] private float gravityAcceleration = 9.81f;

        private float yawDegrees;
        private float commandedYawDegrees;
        private float yawAngularVelocityRadians;
        private float pitchDegrees;
        private float pitchAngularVelocityRadians;
        private float yawTurnInput;
        private bool localAttitudeControl;
        private Quaternion localAttitude = Quaternion.identity;
        private float localPitchRateInput;
        public bool LocalAttitudeControl => localAttitudeControl;
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
        private Quaternion referenceRotation = Quaternion.identity;
        private Vector3 referencePlaneOrigin;
        private uint referenceFrameVersion;
        private Vector3 velocity;
        private bool engineAvailable = true;
        private float quickBoostRemaining;
        private float quickBoostRecoveryRemaining;
        private Vector3 quickBoostDirection;
        private bool assaultBoostActive;
        private float currentManeuverEnergy;
        private float maneuverEnergyRechargeDelayRemaining;
        private bool maneuverEnergyDepleted;
        private bool maneuverEnergyInitialized;
        private MechLoadoutDefinition appliedEnergyLoadout;
        private MechPartDefinition appliedEnergyGenerator;
        private MechPartDefinition appliedEnergyBooster;
        private bool braking;
        private float mainEngineOutput;
        private bool trajectorySyncCommandActive;
        private Vector3 trajectorySyncAccelerationCommand;
        private Vector3 currentTrajectorySyncForceNewtons;
        private Vector3 brakingPlaneAnchor;
        private Vector3 sampledGroundPoint;
        private Vector3 sampledGroundNormal = Vector3.up;
        private bool hasSampledGround;
        private bool isGrounded;
        private Rigidbody massBody;
        private MechRuntimeState runtimeState;
        private float currentMassKg;
        private Vector3 currentControlForceNewtons;
        private Vector3 currentControlAcceleration;
        private Vector3 currentShoulderLiftForceNewtons;
        private Vector3 currentLegLiftForceNewtons;
        private Vector3 currentDescentForceNewtons;
        private Vector3 currentQuickBoostForceNewtons;
        private Vector3 fixedControlCenterLocal;
        private float quickBoostForceNewtons;
        private readonly RaycastHit[] groundHits = new RaycastHit[32];
        private readonly List<MechThrusterChannelState> thrusterChannels = new();
        private readonly List<MechMainThrusterPlume.AuxiliaryNozzleBinding>
            physicalAuxiliaryNozzles = new();
        private MechMainThrusterPlume thrusterPlume;
        private Vector3 currentTranslationInput;
        private Vector3[] translationNozzleDirections = Array.Empty<Vector3>();
        private Vector3[] translationNozzleTorquePerNewton = Array.Empty<Vector3>();
        private float[] translationNozzleCapacities = Array.Empty<float>();
        private float[] translationNozzleOutputs = Array.Empty<float>();
        private bool[] translationNozzleLockedOutputs = Array.Empty<bool>();
        private float translationTorqueReferenceArmMeters = 1f;
        private Vector3 currentAllocationTorqueNewtonMeters;
        private Vector3 currentMainVectorDirectionLocal = Vector3.forward;

        public float YawDegrees => yawDegrees;

        public float LogicalYawDegrees => yawDegrees;

        public float LogicalPitchDegrees => pitchDegrees;

        public float CommandedYawDegrees => commandedYawDegrees;

        public float YawRateDegreesPerSecond => yawAngularVelocityRadians * Mathf.Rad2Deg;

        public float CurrentYawTorqueNewtonMeters => currentYawTorqueNewtonMeters;

        public float CurrentShoulderRcsTorqueNewtonMeters => currentShoulderRcsTorqueNewtonMeters;

        public float CurrentLegRcsTorqueNewtonMeters => currentLegRcsTorqueNewtonMeters;

        public float CurrentMomentumWheelTorqueNewtonMeters => currentMomentumWheelTorqueNewtonMeters;

        public Vector3 Velocity => velocity;
        public bool HasKinematicSample {get;private set;}
        public double KinematicSampleTime {get;private set;}

        public bool EngineAvailable => engineAvailable;

        public float HorizontalSpeed => Vector3.ProjectOnPlane(
            velocity,
            ReferenceUp).magnitude;

        /// <summary>当前主推进器实际输出相对额定主推力的比例，仅用于遥测与热模型。</summary>
        public float MainEngineOutput => mainEngineOutput;

        public float TechnicalSafetySpeed => technicalSafetySpeed;

        public float CurrentManeuverEnergy
        {
            get
            {
                RefreshLoadoutEnergyConfiguration();
                EnsureManeuverEnergyInitialized();
                return currentManeuverEnergy;
            }
        }

        public float MaximumManeuverEnergy
        {
            get
            {
                RefreshLoadoutEnergyConfiguration();
                return Mathf.Max(0.001f, maximumManeuverEnergy);
            }
        }

        public float ManeuverEnergyNormalized =>
            CurrentManeuverEnergy / MaximumManeuverEnergy;

        public bool ManeuverEnergyDepleted => maneuverEnergyDepleted;

        public bool CanQuickBoost
        {
            get
            {
                RefreshLoadoutEnergyConfiguration();
                EnsureManeuverEnergyInitialized();
                return engineAvailable &&
                       quickBoostRecoveryRemaining <= 0f &&
                       !maneuverEnergyDepleted &&
                       currentManeuverEnergy + 0.0001f >= quickBoostEnergyCost;
            }
        }

        public float GravityAcceleration => gravityAcceleration;

        public float CurrentMassKg => ResolveMassKg();

        public Vector3 CurrentControlForceNewtons => currentControlForceNewtons;

        /// <summary>肩部上推喷口本帧施加到机体上的世界空间力。</summary>
        public Vector3 CurrentShoulderLiftForceNewtons => currentShoulderLiftForceNewtons;

        /// <summary>脚部向下排气产生的本帧世界空间上推力；方向由腿部机动姿态决定。</summary>
        public Vector3 CurrentLegLiftForceNewtons => currentLegLiftForceNewtons;

        /// <summary>Upper-body descent/RCS nozzles' downward force for active descent or lift balancing.</summary>
        public Vector3 CurrentDescentForceNewtons => currentDescentForceNewtons;

        public Vector3 CurrentQuickBoostForceNewtons => currentQuickBoostForceNewtons;

        public Vector3 CurrentTrajectorySyncForceNewtons => currentTrajectorySyncForceNewtons;

        public bool TrajectorySyncCommandActive => trajectorySyncCommandActive;

        public Vector3 FixedControlCenterWorld => transform.TransformPoint(fixedControlCenterLocal);

        public Vector3 CurrentAllocationTorqueNewtonMeters => currentAllocationTorqueNewtonMeters;

        public IReadOnlyList<MechThrusterChannelState> ThrusterChannels => thrusterChannels;

        public bool TranslationInputActive => currentTranslationInput.sqrMagnitude > 0.0001f;

        public bool VectorThrustEnabled => vectorThrustEnabled;

        public Vector3 CurrentMainVectorForceDirectionWorld =>
            LogicalRotation * (currentMainVectorDirectionLocal.sqrMagnitude > 0.0001f
                ? currentMainVectorDirectionLocal.normalized
                : Vector3.forward);

        /// <summary>当前帧的持续推进加速度，不含重力与短时 QB。</summary>
        public Vector3 CurrentControlAcceleration => currentControlAcceleration;

        /// <summary>当前 QB 窗口的世界空间加速度；窗口结束后返回零。</summary>
        public Vector3 CurrentQuickBoostAcceleration => currentQuickBoostForceNewtons / ResolveMassKg();

        public float CurrentQuickBoostRemaining => quickBoostRemaining;

        public float CurrentQuickBoostRecoveryRemaining => quickBoostRecoveryRemaining;

        public float AirCoastingDragAcceleration => airCoastingDragAcceleration;

        public float Altitude
        {
            get
            {
                RefreshGroundHeight(transform.position);
                return hasSampledGround
                    ? Mathf.Max(
                        0f,
                        Vector3.Dot(
                            transform.position - sampledGroundPoint,
                            ReferenceUp))
                    : Vector3.Dot(
                        transform.position - referencePlaneOrigin,
                        ReferenceUp);
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

        /// <summary>动态导航参考平面。它不旋转 Unity 世界，只定义飞控的水平面与上方向。</summary>
        public Quaternion ReferenceRotation => referenceRotation;

        public Vector3 ReferenceUp => referenceRotation * Vector3.up;

        public uint ReferenceFrameVersion => referenceFrameVersion;

        /// <summary>完整三维逻辑朝向；滚转包含在动态导航参考平面中，模型轴修正只应用到可见根节点。</summary>
        public Quaternion LogicalRotation => localAttitudeControl
            ? localAttitude
            : referenceRotation *
              Quaternion.AngleAxis(yawDegrees, Vector3.up) *
              Quaternion.AngleAxis(pitchDegrees, Vector3.right);

        public float ModelForwardYawOffset => modelForwardYawOffset;

        private void Awake()
        {
            massBody = GetComponent<Rigidbody>();
            runtimeState = GetComponent<MechRuntimeState>();
            EnsureManeuverEnergyInitialized();
            referencePlaneOrigin = transform.position;
            yawDegrees = Mathf.Repeat(transform.eulerAngles.y - modelForwardYawOffset, 360f);
            commandedYawDegrees = yawDegrees;
            pitchDegrees = 0f;
            aimPitchDegrees = 0f;
            RefreshGroundHeight(transform.position);
            isGrounded = hasSampledGround &&
                         Vector3.Dot(
                             transform.position - sampledGroundPoint,
                             ReferenceUp) <= groundedEpsilon;
            if (isGrounded)
            {
                ResetReferencePlaneToGround(sampledGroundNormal);
            }
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
            var allPartBounds = ResolveAllPartBounds();
            fixedControlCenterLocal = ResolveAssembledCenterOfMassLocal(coreBounds.center);
            if (massBody != null)
            {
                massBody.centerOfMass = fixedControlCenterLocal;
            }

            attitudeWidthMeters = Mathf.Max(0.5f, allPartBounds.size.x);
            attitudeDepthMeters = Mathf.Max(0.5f, allPartBounds.size.z);
            translationTorqueReferenceArmMeters = Mathf.Max(1f, allPartBounds.extents.magnitude);

            var logicalRight = LogicalRotation * Vector3.right;
            var logicalForward = LogicalRotation * Vector3.forward;
            var coreHalfWidth = Mathf.Max(0.6f, coreBounds.extents.x * 0.65f);
            var coreHalfDepth = Mathf.Max(0.45f, coreBounds.extents.z * 0.7f);
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

            if (leftMainAnchor != null)
            {
                AddThrusterChannel(
                    "main.left", MechThrusterRole.Main, MechThrusterSide.Left,
                    leftMainAnchor, leftMainFallback, false, previousHealth);
            }
            if (rightMainAnchor != null)
            {
                AddThrusterChannel(
                    "main.right", MechThrusterRole.Main, MechThrusterSide.Right,
                    rightMainAnchor, rightMainFallback, false, previousHealth);
            }

            AddPhysicalAuxiliaryThrusterChannels(previousHealth);
        }

        private MechPartDamageController partDamage;
        private float ResolveThrusterEfficiency(MechThrusterChannelState channel)
        {
            if(partDamage==null)partDamage=GetComponent<MechPartDamageController>();
            if(partDamage==null||!partDamage.IsBound)return channel.Health;
            var side=channel.Side==MechThrusterSide.Left?MechDamageSide.Left:channel.Side==MechThrusterSide.Right?MechDamageSide.Right:MechDamageSide.Center;
            var owner=channel.Anchor!=null?channel.Anchor.GetComponentInParent<MechAssembledPart>():null;
            MechPartSlot role=owner!=null?owner.Slot:
                channel.Role==MechThrusterRole.Main||channel.Role==MechThrusterRole.LegLift||channel.Role==MechThrusterRole.LegRcs?MechPartSlot.Legs:MechPartSlot.Core;
            if(channel.Anchor!=null&&runtimeState!=null)
            {
                float x=transform.InverseTransformPoint(channel.Anchor.position).x*MechDamageGeometry.LeftSign(runtimeState);
                if(Mathf.Abs(x)>.01f)side=x>=0?MechDamageSide.Left:MechDamageSide.Right;
            }
            float factor=partDamage.RoleEfficiency(role,side);
            if(channel.Role==MechThrusterRole.Main)factor*=partDamage.RoleEfficiency(MechPartSlot.Booster);
            return channel.Health*factor;
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
            pitchDegrees = 0f;
            pitchAngularVelocityRadians = 0f;
            localAttitude = referenceRotation;
            localPitchRateInput = 0f;
            ApplyVisualRotation();
        }

        // The scalar API remains reference-relative for AI/legacy autopilots.
        // Player input uses local angular rates instead of reconstructing Euler angles.
        public void SetAimPitch(float pitchDegrees)
        {
            localAttitudeControl = false;
            localPitchRateInput = 0f;
            aimPitchDegrees = ConstrainPitchForCurrentMode(pitchDegrees);
        }

        public void EnableLocalAttitudeControl(float initialViewPitch)
        {
            if (localAttitudeControl) return;
            localAttitude = ResolveReferenceViewRotation(ConstrainPitchForCurrentMode(initialViewPitch));
            localAttitudeControl = true;
            UpdateLocalAttitudeReadout();
        }

        public void SetLocalAttitudeInput(float yawInput, float pitchRateDegrees)
        {
            if (!localAttitudeControl)
            {
                localAttitude = LogicalRotation;
                localAttitudeControl = true;
            }
            yawTurnInput = float.IsFinite(yawInput) ? Mathf.Clamp(yawInput, -1f, 1f) : 0f;
            localPitchRateInput = float.IsFinite(pitchRateDegrees) ? pitchRateDegrees : 0f;
        }

        public void StopLocalAttitudeInput()
        {
            yawTurnInput = 0f;
            localPitchRateInput = 0f;
        }

        private void UpdateLocalAttitudeReadout()
        {
            var forward = Quaternion.Inverse(referenceRotation) * (localAttitude * Vector3.forward);
            var horizontal = Mathf.Sqrt(forward.x * forward.x + forward.z * forward.z);
            if (horizontal > 0.001f)
                yawDegrees = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);
            pitchDegrees = -Mathf.Atan2(forward.y, horizontal) * Mathf.Rad2Deg;
            commandedYawDegrees = yawDegrees;
            aimPitchDegrees = pitchDegrees;
        }

        private void IntegrateLocalAttitude(float deltaTime, float maximumTurnRate)
        {
            var rates = new Vector3(
                Mathf.Clamp(localPitchRateInput, -maximumTurnRate, maximumTurnRate),
                yawTurnInput * maximumTurnRate, 0f);
            if (isGrounded)
            {
                // Clamp BEFORE crossing the pole; extracting Euler elevation
                // afterwards would fold an illegal -103 degrees back into -77.
                UpdateLocalAttitudeReadout();
                yawDegrees = Mathf.Repeat(yawDegrees + rates.y * deltaTime, 360f);
                pitchDegrees = ConstrainPitchForCurrentMode(pitchDegrees + rates.x * deltaTime);
                localAttitude = referenceRotation * Quaternion.AngleAxis(yawDegrees, Vector3.up) *
                                Quaternion.AngleAxis(pitchDegrees, Vector3.right);
            }
            else
            {
                var speed = rates.magnitude;
                if (speed > 0.0001f && deltaTime > 0f)
                    localAttitude = Quaternion.Normalize(localAttitude *
                        Quaternion.AngleAxis(speed * deltaTime, rates / speed));
            }
            UpdateLocalAttitudeReadout();
            yawAngularVelocityRadians = rates.y * Mathf.Deg2Rad;
            pitchAngularVelocityRadians = rates.x * Mathf.Deg2Rad;
        }

        public float ConstrainPitchForCurrentMode(float requestedPitchDegrees)
        {
            var normalizedPitch = Mathf.DeltaAngle(0f, requestedPitchDegrees);
            if (!isGrounded)
            {
                return normalizedPitch;
            }

            return Mathf.Clamp(
                normalizedPitch,
                -Mathf.Clamp(maximumGroundedClimbPitchDegrees, 0f, 89f),
                Mathf.Clamp(maximumGroundedDivePitchDegrees, 0f, 30f));
        }

        public Quaternion ResolveReferenceViewRotation(float viewPitchDegrees) =>
            localAttitudeControl ? localAttitude : referenceRotation *
            Quaternion.AngleAxis(yawDegrees, Vector3.up) *
            Quaternion.AngleAxis(viewPitchDegrees, Vector3.right);

        public void ResetReferencePlaneToRotation(Quaternion levelRotation)
        {
            var forward = levelRotation * Vector3.forward;
            var up = levelRotation * Vector3.up;
            SetReferencePlane(forward, up);
        }

        public void ResetReferencePlaneToward(Vector3 worldTargetPosition)
        {
            var direction = worldTargetPosition - transform.position;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            direction.Normalize();
            var up = Vector3.ProjectOnPlane(
                localAttitudeControl ? LogicalRotation * Vector3.up : ReferenceUp, direction);
            if (up.sqrMagnitude <= 0.0001f)
            {
                up = Vector3.ProjectOnPlane(LogicalRotation * Vector3.up, direction);
            }
            if (up.sqrMagnitude <= 0.0001f)
            {
                up = Vector3.Cross(direction, LogicalRotation * Vector3.right);
            }

            SetReferencePlane(direction, up);
        }

        private void ResetReferencePlaneToGround(Vector3 groundNormal)
        {
            var up = groundNormal.sqrMagnitude > 0.0001f
                ? groundNormal.normalized
                : Vector3.up;
            var forward = Vector3.ProjectOnPlane(
                LogicalRotation * Vector3.forward,
                up);
            if (forward.sqrMagnitude <= 0.0001f)
            {
                forward = Vector3.ProjectOnPlane(ReferenceRotation * Vector3.forward, up);
            }

            SetReferencePlane(forward, up);
        }

        private void SetReferencePlane(Vector3 forward, Vector3 up)
        {
            if (up.sqrMagnitude <= 0.0001f)
            {
                up = Vector3.up;
            }
            up.Normalize();
            forward = Vector3.ProjectOnPlane(forward, up);
            if (forward.sqrMagnitude <= 0.0001f)
            {
                forward = Vector3.ProjectOnPlane(Vector3.forward, up);
            }
            if (forward.sqrMagnitude <= 0.0001f)
            {
                forward = Vector3.Cross(Vector3.right, up);
            }

            referenceRotation = Quaternion.LookRotation(forward.normalized, up);
            referencePlaneOrigin = transform.position;
            yawDegrees = 0f;
            commandedYawDegrees = 0f;
            yawAngularVelocityRadians = 0f;
            pitchDegrees = 0f;
            aimPitchDegrees = 0f;
            pitchAngularVelocityRadians = 0f;
            localAttitude = referenceRotation;
            StopLocalAttitudeInput();
            referenceFrameVersion++;
            ApplyVisualRotation();
        }

        public void SetYawTurnInput(float normalizedInput)
        {
            yawTurnInput = Mathf.Clamp(normalizedInput, -1f, 1f);
        }

        /// <summary>
        /// 绕当前机体前向滚转导航参考平面。滚转会同步改变机体、镜头与推进坐标系，
        /// 但不触发参考平面重置事件，避免拖动期间清空当前俯仰和瞄准输入。
        /// </summary>
        public void RollReferencePlane(float deltaDegrees)
        {
            if (!float.IsFinite(deltaDegrees) || Mathf.Abs(deltaDegrees) <= 0.0001f)
            {
                return;
            }

            var forward = LogicalRotation * Vector3.forward;
            if (forward.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            var roll = Quaternion.AngleAxis(deltaDegrees, forward.normalized);
            referenceRotation = Quaternion.Normalize(roll * referenceRotation);
            if (localAttitudeControl)
            {
                localAttitude = Quaternion.Normalize(roll * localAttitude);
                UpdateLocalAttitudeReadout();
            }
            ApplyVisualRotation();
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
            EnsureManeuverEnergyInitialized();
            assaultBoostActive = active && engineAvailable &&
                                   !maneuverEnergyDepleted &&
                                   currentManeuverEnergy > 0f;
            if (assaultBoostActive)
            {
                quickBoostRemaining = 0f;
            }
        }

        public void SetEngineAvailable(bool available)
        {
            engineAvailable = available;
            if (available)
            {
                return;
            }

            assaultBoostActive = false;
            quickBoostRemaining = 0f;
            braking = false;
            mainEngineOutput = 0f;
            currentControlForceNewtons = Vector3.zero;
            currentControlAcceleration = Vector3.zero;
            currentShoulderLiftForceNewtons = Vector3.zero;
            currentLegLiftForceNewtons = Vector3.zero;
            currentDescentForceNewtons = Vector3.zero;
            currentQuickBoostForceNewtons = Vector3.zero;
            currentTrajectorySyncForceNewtons = Vector3.zero;
        }

        public void ConfigureTechnicalSafetySpeed(float safetySpeed)
        {
            technicalSafetySpeed = Mathf.Max(0f, safetySpeed);
        }

        public void ConfigureQuickBoost(float targetVelocityChange, float duration)
        {
            quickBoostThrustKn = duration > Mathf.Epsilon
                ? Mathf.Max(0f, targetVelocityChange) * ResolveMassKg() / duration / 1000f
                : 0f;
            quickBoostDuration = Mathf.Max(0f, duration);
        }

        public void ConfigureManeuverEnergy(
            float capacity,
            float movementDrainPerSecond,
            float assaultBoostDrainPerSecond,
            float quickBoostCost,
            float rechargePerSecond,
            float rechargeDelay,
            float restartFraction)
        {
            maximumManeuverEnergy = Mathf.Max(1f, capacity);
            movementEnergyDrainPerSecond = Mathf.Max(0f, movementDrainPerSecond);
            assaultBoostEnergyDrainPerSecond =
                Mathf.Max(0f, assaultBoostDrainPerSecond);
            quickBoostEnergyCost = Mathf.Clamp(
                quickBoostCost,
                0f,
                maximumManeuverEnergy);
            maneuverEnergyRechargePerSecond = Mathf.Max(0f, rechargePerSecond);
            maneuverEnergyRechargeDelay = Mathf.Max(0f, rechargeDelay);
            maneuverEnergyRestartFraction = Mathf.Clamp01(restartFraction);
            currentManeuverEnergy = maximumManeuverEnergy;
            maneuverEnergyRechargeDelayRemaining = 0f;
            maneuverEnergyDepleted = false;
            maneuverEnergyInitialized = true;
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

        public void SetTrajectorySyncAcceleration(Vector3 acceleration)
        {
            trajectorySyncCommandActive = true;
            trajectorySyncAccelerationCommand = IsFinite(acceleration)
                ? acceleration
                : Vector3.zero;
        }

        public void ClearTrajectorySyncCommand()
        {
            trajectorySyncCommandActive = false;
            trajectorySyncAccelerationCommand = Vector3.zero;
        }

        public float GetAvailableDirectionalAcceleration(Vector3 worldDirection)
        {
            if (worldDirection.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            EnsureThrusterLayout();
            ResolveThrusterForces(
                out var mainNewtons,
                out var lateralNewtons,
                out var reverseNewtons,
                out var upwardNewtons,
                out var downwardNewtons);
            var localDirection =
                Quaternion.Inverse(LogicalRotation) *
                worldDirection.normalized;
            var forceCapacity = 0f;
            if (Mathf.Abs(localDirection.z) > 0.0001f)
            {
                var role = localDirection.z >= 0f
                    ? MechThrusterRole.Main
                    : MechThrusterRole.Reverse;
                var rated = localDirection.z >= 0f
                    ? mainNewtons
                    : reverseNewtons;
                forceCapacity +=
                    Mathf.Abs(localDirection.z) *
                    ResolveThrusterForceCapacity(role, rated, true);
            }

            if (Mathf.Abs(localDirection.x) > 0.0001f)
            {
                var role = localDirection.x >= 0f
                    ? MechThrusterRole.StrafePositive
                    : MechThrusterRole.StrafeNegative;
                forceCapacity +=
                    Mathf.Abs(localDirection.x) *
                    ResolveThrusterForceCapacity(
                        role,
                        lateralNewtons,
                        false);
            }

            if (localDirection.y > 0.0001f)
            {
                var shoulderRated = upwardNewtons * Mathf.Clamp01(shoulderLiftFraction);
                var legRated = upwardNewtons * (1f - Mathf.Clamp01(shoulderLiftFraction));
                forceCapacity += localDirection.y *
                    (ResolveThrusterForceCapacity(
                         MechThrusterRole.ShoulderLift,
                         shoulderRated,
                         true) +
                     ResolveThrusterForceCapacity(
                         MechThrusterRole.LegLift,
                         legRated,
                         true));
            }
            else if (localDirection.y < -0.0001f)
            {
                forceCapacity += -localDirection.y *
                    ResolveThrusterForceCapacity(
                        MechThrusterRole.Descent,
                        downwardNewtons,
                        true);
            }

            return forceCapacity * ResolveOverloadAccelerationScale() /
                   Mathf.Max(1f, ResolveMassKg());
        }

        /// <summary>Axial support estimate from live nozzles for autonomous braking decisions.</summary>
        public float EstimateTranslationAcceleration(Vector3 worldDirection)
        {
            if (!engineAvailable || !IsFinite(worldDirection) || worldDirection.sqrMagnitude < 0.0001f)
                return 0f;
            EnsureThrusterLayout();
            ResolveThrusterForces(out var mainForce, out _, out _, out _, out _);
            var mainCount = 0;
            foreach (var channel in thrusterChannels)
                if (channel.Operational && channel.Anchor != null &&
                    channel.Id.StartsWith("main.", StringComparison.Ordinal)) mainCount++;
            var direction = worldDirection.normalized;
            var projectedForce = 0f;
            thrusterPlume ??= GetComponent<MechMainThrusterPlume>();
            foreach (var channel in thrusterChannels)
            {
                if (!channel.Operational || channel.Anchor == null) continue;
                Vector3 nozzleDirection;
                float capacity;
                if (channel.Id.StartsWith("main.", StringComparison.Ordinal))
                {
                    nozzleDirection = LogicalRotation * Vector3.forward;
                    capacity = mainForce * ResolveOverloadAccelerationScale() / Mathf.Max(1, mainCount);
                }
                else if (thrusterPlume != null && thrusterPlume.TryResolveAuxiliaryNozzleAllocation(
                             channel.Anchor, direction, out nozzleDirection, out var thrustKn))
                {
                    capacity = thrustKn * 1000f;
                }
                else
                {
                    nozzleDirection = -channel.Anchor.forward;
                    capacity = auxiliaryNozzleThrustKn * 1000f;
                }
                projectedForce += Mathf.Max(0f, Vector3.Dot(direction, nozzleDirection)) *
                                  Mathf.Max(0f, capacity) * ResolveThrusterEfficiency(channel);
            }
            return projectedForce / Mathf.Max(1f, ResolveMassKg());
        }

        public void ConfigureGravity(float acceleration)
        {
            gravityAcceleration = Mathf.Max(0f, acceleration);
        }

        public void Tick(in MechPlayerInputFrame frame, float deltaTime)
        {
            HasKinematicSample=true;
            KinematicSampleTime=Time.timeAsDouble;
            currentTranslationInput = new Vector3(
                Mathf.Clamp(frame.MoveLocal.x, -1f, 1f),
                (frame.RiseHeld ? 1f : 0f) - (frame.DescendHeld ? 1f : 0f),
                Mathf.Clamp(frame.MoveLocal.y, -1f, 1f));
            EnsureThrusterLayout();
            RefreshLoadoutEnergyConfiguration();
            EnsureManeuverEnergyInitialized();
            UpdateManeuverEnergyRecovery(deltaTime);
            quickBoostRecoveryRemaining = Mathf.Max(
                0f,
                quickBoostRecoveryRemaining - Mathf.Max(0f, deltaTime));
            ClearThrusterForces();
            currentMassKg = ResolveMassKg();
            currentControlForceNewtons = Vector3.zero;
            currentControlAcceleration = Vector3.zero;
            currentShoulderLiftForceNewtons = Vector3.zero;
            currentLegLiftForceNewtons = Vector3.zero;
            currentDescentForceNewtons = Vector3.zero;
            currentQuickBoostForceNewtons = Vector3.zero;
            currentTrajectorySyncForceNewtons = Vector3.zero;
            mainEngineOutput = 0f;

            if (!engineAvailable)
            {
                assaultBoostActive = false;
                quickBoostRemaining = 0f;
                braking = false;
                return;
            }

            // Cursor displacement commands bounded yaw and pitch rates in the active reference
            // frame. Player roll input rotates that reference frame separately.
            var maximumTurnRateDegrees = ResolveMaximumYawRateDegrees();
            if (localAttitudeControl)
            {
                IntegrateLocalAttitude(Mathf.Max(0f, deltaTime), maximumTurnRateDegrees);
            }
            else
            {
                var desiredYawRateDegrees = yawTurnInput * maximumTurnRateDegrees;
                var currentYawRateDegrees = desiredYawRateDegrees;
                if (Mathf.Abs(desiredYawRateDegrees) < 0.01f &&
                    Mathf.Abs(currentYawRateDegrees) < 0.01f)
                {
                    currentYawRateDegrees = 0f;
                }

                yawDegrees = Mathf.Repeat(
                    yawDegrees + currentYawRateDegrees * Mathf.Max(0f, deltaTime),
                    360f);
                commandedYawDegrees = yawDegrees;
                yawAngularVelocityRadians = currentYawRateDegrees * Mathf.Deg2Rad;

                aimPitchDegrees = ConstrainPitchForCurrentMode(aimPitchDegrees);
                if (isGrounded)
                {
                    var constrainedPitch = ConstrainPitchForCurrentMode(pitchDegrees);
                    if (!Mathf.Approximately(constrainedPitch, pitchDegrees))
                    {
                        pitchDegrees = constrainedPitch;
                        pitchAngularVelocityRadians = 0f;
                    }
                }

                var pitchErrorDegrees = Mathf.DeltaAngle(pitchDegrees, aimPitchDegrees);
                var desiredPitchRateDegrees = Mathf.Clamp(
                    pitchErrorDegrees / Mathf.Max(0.06f, yawResponseTimeSeconds),
                    -maximumTurnRateDegrees,
                    maximumTurnRateDegrees);
                var currentPitchRateDegrees = desiredPitchRateDegrees;
                var pitchStepDegrees =
                    currentPitchRateDegrees * Mathf.Max(0f, deltaTime);
                if (Mathf.Abs(pitchStepDegrees) >= Mathf.Abs(pitchErrorDegrees))
                {
                    pitchDegrees = aimPitchDegrees;
                    currentPitchRateDegrees = 0f;
                }
                else
                {
                    pitchDegrees = Mathf.DeltaAngle(
                        0f,
                        pitchDegrees + pitchStepDegrees);
                }
                pitchAngularVelocityRadians =
                    currentPitchRateDegrees * Mathf.Deg2Rad;
            }
            currentYawTorqueNewtonMeters = 0f;
            currentShoulderRcsTorqueNewtonMeters = 0f;
            currentLegRcsTorqueNewtonMeters = 0f;
            currentMomentumWheelTorqueNewtonMeters = 0f;
            if (frame.BrakeHeld && !braking)
            {
                brakingPlaneAnchor = transform.position;
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
            var attitudeBasis = LogicalRotation;
            var forwardCommand = Mathf.Clamp(frame.MoveLocal.y, -1f, 1f);
            var movementEnergyScale = ConsumeMovementEnergy(
                ResolveMovementEnergyDrainRate(in frame, forwardCommand),
                deltaTime);
            ResolveThrusterForces(
                out var mainThrustNewtons,
                out var lateralThrustNewtons,
                out var reverseThrustNewtons,
                out var upwardThrustNewtons,
                out var downwardThrustNewtons);
            var overloadAccelerationScale = ResolveOverloadAccelerationScale();
            mainThrustNewtons *= overloadAccelerationScale;
            lateralThrustNewtons *= overloadAccelerationScale;
            reverseThrustNewtons *= overloadAccelerationScale;
            upwardThrustNewtons *= overloadAccelerationScale;
            downwardThrustNewtons *= overloadAccelerationScale;
            mainThrustNewtons *= movementEnergyScale;
            lateralThrustNewtons *= movementEnergyScale;
            reverseThrustNewtons *= movementEnergyScale;
            upwardThrustNewtons *= movementEnergyScale;
            downwardThrustNewtons *= movementEnergyScale;

            // Unity local axes are authoritative for translation:
            // X = left/right, Y = down/up, Z = backward/forward.
            var localRequestedForce = new Vector3(
                frame.MoveLocal.x * lateralThrustNewtons,
                currentTranslationInput.y >= 0f
                    ? currentTranslationInput.y * upwardThrustNewtons
                    : currentTranslationInput.y * downwardThrustNewtons,
                forwardCommand >= 0f
                    ? forwardCommand * mainThrustNewtons
                    : forwardCommand * reverseThrustNewtons);
            if (AssaultBoostActive && !braking)
            {
                localRequestedForce.z += assaultBoostThrustKn * 1000f;
            }

            var requestedTranslationForce = !braking
                ? attitudeBasis * localRequestedForce
                : Vector3.zero;
            if (!braking && frame.HasAutonomousFlightCommand)
            {
                // MoveLocal/RiseHeld remain animation and takeoff cues for AI.
                // Its continuous 3D request replaces those digital thrust commands.
                requestedTranslationForce = IsFinite(frame.AutonomousAccelerationWorld)
                    ? frame.AutonomousAccelerationWorld * currentMassKg * movementEnergyScale
                    : Vector3.zero;
            }
            var requestedSyncForce = Vector3.zero;
            if (!braking && !frame.HasAutonomousFlightCommand && trajectorySyncCommandActive &&
                trajectorySyncAccelerationCommand.sqrMagnitude > 0.0001f)
            {
                requestedSyncForce = trajectorySyncAccelerationCommand * currentMassKg;
                requestedTranslationForce += requestedSyncForce;
            }

            var quickBoostApplying = quickBoostStep > 0f;
            var requestedQuickBoostForce = Vector3.zero;
            if (!braking && quickBoostApplying &&
                quickBoostDirection.sqrMagnitude > 0.0001f && deltaTime > Mathf.Epsilon)
            {
                // Scale the force by its active fraction this frame so the integrated impulse
                // remains identical when the final QB frame is shorter than deltaTime.
                requestedQuickBoostForce = quickBoostDirection.normalized
                                           * quickBoostForceNewtons
                                           * Mathf.Clamp01(quickBoostStep / deltaTime);
                requestedTranslationForce += requestedQuickBoostForce;
            }

            if (braking)
            {
                var planarVelocity = Vector3.ProjectOnPlane(velocity, ReferenceUp);
                if (planarVelocity.sqrMagnitude > 0.0001f)
                {
                    var brakingDirection = -planarVelocity.normalized;
                    var brakingCapacity = ResolveDirectionalForceCapacity(
                        brakingDirection,
                        attitudeBasis,
                        mainThrustNewtons,
                        lateralThrustNewtons,
                        reverseThrustNewtons,
                        upwardThrustNewtons,
                        downwardThrustNewtons);
                    requestedTranslationForce = brakingDirection
                                                * brakingCapacity
                                                * activeBrakingThrustMultiplier;
                }
            }

            var propulsionForce = AllocateTranslationForce(
                requestedTranslationForce,
                mainThrustNewtons + (AssaultBoostActive
                    ? assaultBoostThrustKn * 1000f
                    : 0f),
                deltaTime,
                frame.HasAutonomousFlightCommand ? movementEnergyScale : 1f);
            var hasPropulsionCommand = propulsionForce.sqrMagnitude > 0.0001f;
            currentControlForceNewtons = propulsionForce;
            currentControlAcceleration = propulsionForce / currentMassKg;

            var requestRealization = requestedTranslationForce.sqrMagnitude > Mathf.Epsilon
                ? Mathf.Clamp01(Vector3.Dot(
                    propulsionForce,
                    requestedTranslationForce) / requestedTranslationForce.sqrMagnitude)
                : 0f;
            currentTrajectorySyncForceNewtons = requestedSyncForce * requestRealization;
            currentQuickBoostForceNewtons = requestedQuickBoostForce * requestRealization;

            var verticalForce = Vector3.Project(
                propulsionForce,
                attitudeBasis * Vector3.up);
            if (Vector3.Dot(verticalForce, attitudeBasis * Vector3.up) >= 0f)
            {
                currentLegLiftForceNewtons = verticalForce;
            }
            else
            {
                currentDescentForceNewtons = verticalForce;
            }

            if (!braking)
            {
                velocity += currentControlAcceleration * deltaTime;
            }

            if (!braking && !grounded)
            {
                velocity -= ReferenceUp * (gravityAcceleration * deltaTime);
            }

            if (braking)
            {
                var planarVelocity = Vector3.ProjectOnPlane(
                    velocity,
                    ReferenceUp);
                var previousPlanarVelocity = planarVelocity;
                planarVelocity += Vector3.ProjectOnPlane(
                    currentControlAcceleration,
                    ReferenceUp) * deltaTime;
                if (Vector3.Dot(previousPlanarVelocity, planarVelocity) <= 0f)
                {
                    planarVelocity = Vector3.zero;
                }
                if (planarVelocity.magnitude <= groundStopSpeed)
                {
                    planarVelocity = Vector3.zero;
                }

                velocity = planarVelocity;
            }

            mainEngineOutput = Mathf.Clamp01(
                SumThrusterForce(MechThrusterRole.Main).magnitude /
                Mathf.Max(1f, mainThrustNewtons));

            // 无输入时空中保留惯性；地面则由接触摩擦可靠退出 QB/滑行。
            if (!braking && grounded && !hasPropulsionCommand &&
                !AssaultBoostActive && !quickBoostApplying)
            {
                var groundNormal = sampledGroundNormal.sqrMagnitude > 0.0001f
                    ? sampledGroundNormal
                    : ReferenceUp;
                var planarVelocity = Vector3.ProjectOnPlane(velocity, groundNormal);
                planarVelocity = Vector3.MoveTowards(
                    planarVelocity, Vector3.zero, groundBrakingAcceleration * deltaTime);
                if (planarVelocity.magnitude <= groundStopSpeed)
                {
                    planarVelocity = Vector3.zero;
                }

                velocity = planarVelocity;
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
                var planeOffset = Vector3.Dot(
                    position - brakingPlaneAnchor,
                    ReferenceUp);
                position -= ReferenceUp * planeOffset;
                velocity = Vector3.ProjectOnPlane(velocity, ReferenceUp);
            }

            var groundUp = ReferenceUp;
            var hasGround = TrySampleGroundSurface(
                position,
                groundUp,
                out var surfacePoint,
                out var surfaceNormal);
            var surfaceDistance = hasGround
                ? Vector3.Dot(position - surfacePoint, groundUp)
                : float.PositiveInfinity;
            if (braking)
            {
                isGrounded = hasGround &&
                             surfaceDistance <= groundedEpsilon;
            }
            else if (grounded && !frame.RiseHeld)
            {
                var surfaceDelta = hasGround
                    ? Vector3.Dot(surfacePoint - previousPosition, groundUp)
                    : float.NegativeInfinity;
                if (hasGround &&
                    surfaceDelta >= -maximumGroundSnapDown &&
                    surfaceDelta <= maximumGroundSnapUp)
                {
                    // Follow a nearby surface, and preserve its tangent as real velocity.
                    // At a ramp edge the sampled floor drops beyond maximumGroundSnapDown,
                    // so this vertical component survives and launches the mech naturally.
                    position -= groundUp * surfaceDistance;
                    velocity = Vector3.ProjectOnPlane(velocity, groundUp) +
                               groundUp * (deltaTime > Mathf.Epsilon
                                   ? surfaceDelta / deltaTime
                                   : 0f);
                    isGrounded = true;
                }
                else
                {
                    isGrounded = false;
                }
            }
            else if (hasGround &&
                     !braking &&
                     !frame.RiseHeld &&
                     Vector3.Dot(velocity, groundUp) <= 0f &&
                     surfaceDistance <= 0f)
            {
                position -= groundUp * surfaceDistance;
                velocity = Vector3.ProjectOnPlane(velocity, groundUp);
                isGrounded = true;
            }
            else if (!braking)
            {
                isGrounded = false;
            }

            hasSampledGround = hasGround;
            sampledGroundPoint = hasGround ? surfacePoint : position;
            sampledGroundNormal = surfaceNormal;

            if (isGrounded &&
                (!grounded || Vector3.Angle(ReferenceUp, surfaceNormal) > 0.5f))
            {
                ResetReferencePlaneToGround(surfaceNormal);
            }

            transform.SetPositionAndRotation(
                position,
                LogicalRotation *
                Quaternion.AngleAxis(modelForwardYawOffset, Vector3.up));
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
            var maximumRate = ResolveMaximumYawRateDegrees() * Mathf.Deg2Rad;
			var totalRcsThrustKn = ResolveRcsThrustKn();
			var configuredRcsTotal = Mathf.Max(0.001f, shoulderRcsThrustKn + legRcsThrustKn);
			var shoulderRcsKn = totalRcsThrustKn * Mathf.Max(0f, shoulderRcsThrustKn) / configuredRcsTotal;
			var legRcsKn = totalRcsThrustKn * Mathf.Max(0f, legRcsThrustKn) / configuredRcsTotal;
            var shoulderCapacity = ResolveYawRcsTorqueCapacity(
                MechThrusterRole.ShoulderRcs,
                shoulderRcsKn * 1000f,
                shoulderRcsLeverArmMeters);
            var legCapacity = ResolveYawRcsTorqueCapacity(
                MechThrusterRole.LegRcs,
                legRcsKn * 1000f,
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
                shoulderRcsKn * 1000f,
                shoulderRcsLeverArmMeters);
            remainingTorque -= currentShoulderRcsTorqueNewtonMeters;
            currentLegRcsTorqueNewtonMeters = AllocateYawRcsTorque(
                MechThrusterRole.LegRcs,
                remainingTorque,
                legRcsKn * 1000f,
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

                if (channel.Side == MechThrusterSide.Left) leftHealth += ResolveThrusterEfficiency(channel);
                if (channel.Side == MechThrusterSide.Right) rightHealth += ResolveThrusterEfficiency(channel);
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
            var torqueSign = Mathf.Sign(appliedTorque);
            var usableLeverArmSum = 0f;
            var eligibleChannelCount = 0;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role || !channel.Operational)
                {
                    continue;
                }

                var radial = channel.ResolveWorldOrigin(transform) - controlCenter;
                radial = Vector3.ProjectOnPlane(radial, ReferenceUp);
                if (radial.sqrMagnitude <= Mathf.Epsilon)
                {
                    continue;
                }

                var forceDirection = Vector3.Cross(ReferenceUp, radial).normalized * torqueSign;
                if (!CanThrusterChannelProvideForce(channel, forceDirection))
                {
                    continue;
                }

                usableLeverArmSum += radial.magnitude;
                eligibleChannelCount++;
            }

            if (usableLeverArmSum <= Mathf.Epsilon)
            {
                return 0f;
            }

            var forceMagnitude = Mathf.Abs(appliedTorque) / usableLeverArmSum;
            var ratedForcePerChannel = eligibleChannelCount > 0
                ? Mathf.Max(0f, ratedGroupForceNewtons) / eligibleChannelCount
                : 0f;
            var realizedTorque = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role || !channel.Operational)
                {
                    continue;
                }

                var radial = channel.ResolveWorldOrigin(transform) - controlCenter;
                radial = Vector3.ProjectOnPlane(radial, ReferenceUp);
                if (radial.sqrMagnitude <= Mathf.Epsilon)
                {
                    continue;
                }

                // F = up x r guarantees a positive yaw torque in the active reference frame.
                var forceDirection = Vector3.Cross(ReferenceUp, radial).normalized * torqueSign;
                if (!CanThrusterChannelProvideForce(channel, forceDirection))
                {
                    continue;
                }

                var force = forceDirection * forceMagnitude;
                channel.AddForce(
                    force,
                    ratedForcePerChannel * ResolveThrusterEfficiency(channel));
                realizedTorque += Vector3.Dot(
                    Vector3.Cross(radial, force),
                    ReferenceUp);
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
                if (thrusterChannels[i].Role != role ||
                    !CanThrusterChannelProvideForce(
                        thrusterChannels[i],
                        requestedForce))
                {
                    continue;
                }

                channelCount++;
                if (thrusterChannels[i].Side == MechThrusterSide.Left) leftCount++;
                if (thrusterChannels[i].Side == MechThrusterSide.Right) rightCount++;
            }

            if (channelCount == 0)
            {
                return Vector3.zero;
            }

            var leftCapacity = 0f;
            var rightCapacity = 0f;
            var centerCapacity = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Role != role ||
                    !CanThrusterChannelProvideForce(channel, requestedForce))
                {
                    continue;
                }

                float nominalCapacity;
                switch (channel.Side)
                {
                    case MechThrusterSide.Left when leftCount > 0 && rightCount > 0:
                        nominalCapacity = ratedMagnitude * 0.5f / leftCount;
                        leftCapacity += nominalCapacity * ResolveThrusterEfficiency(channel);
                        break;
                    case MechThrusterSide.Right when leftCount > 0 && rightCount > 0:
                        nominalCapacity = ratedMagnitude * 0.5f / rightCount;
                        rightCapacity += nominalCapacity * ResolveThrusterEfficiency(channel);
                        break;
                    default:
                        nominalCapacity = ratedMagnitude / channelCount;
                        centerCapacity += nominalCapacity * ResolveThrusterEfficiency(channel);
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
                if (channel.Role != role ||
                    !channel.Operational ||
                    !CanThrusterChannelProvideForce(channel, requestedForce))
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

                var channelCapacity = nominalCapacity * ResolveThrusterEfficiency(channel);
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
                    direction * (appliedMagnitude * channelAllowedCapacity / effectiveCapacity),
                    channelAllowedCapacity);
            }

            return direction * appliedMagnitude;
        }

        private Vector3 AllocateTranslationForce(
            Vector3 requestedForce,
            float ratedMainForceNewtons,
            float deltaTime,
            float auxiliaryCapacityScale = 1f)
        {
            var channelCount = thrusterChannels.Count;
            EnsureTranslationAllocatorBuffers(channelCount);
            var mainForceDirection = ResolveMainVectorForceDirection(
                requestedForce,
                deltaTime);

            var operationalMainCount = 0;
            for (var i = 0; i < channelCount; i++)
            {
                var channel = thrusterChannels[i];
                if (channel.Operational &&
                    channel.Anchor != null &&
                    channel.Id.StartsWith("main.", StringComparison.Ordinal))
                {
                    operationalMainCount++;
                }
            }

            var mainCapacityPerNozzle = operationalMainCount > 0
                ? Mathf.Max(0f, ratedMainForceNewtons) / operationalMainCount
                : 0f;
            thrusterPlume ??= GetComponent<MechMainThrusterPlume>();
            for (var i = 0; i < channelCount; i++)
            {
                var channel = thrusterChannels[i];
                var anchor = channel.Anchor;
                var capacity = 0f;
                var direction = Vector3.zero;
                if (channel.Operational && anchor != null)
                {
                    var isMain = channel.Id.StartsWith(
                        "main.",
                        StringComparison.Ordinal);
                    if (isMain)
                    {
                        capacity = mainCapacityPerNozzle * ResolveThrusterEfficiency(channel);
                        direction = mainForceDirection;
                    }
                    else if (thrusterPlume != null &&
                             thrusterPlume.TryResolveAuxiliaryNozzleAllocation(
                                 anchor,
                                 requestedForce,
                                 out var resolvedDirection,
                                 out var ratedThrustKn))
                    {
                        capacity = ratedThrustKn * 1000f * ResolveThrusterEfficiency(channel) * auxiliaryCapacityScale;
                        direction = resolvedDirection;
                    }
                    else
                    {
                        capacity = Mathf.Max(0f, auxiliaryNozzleThrustKn)
                                   * 1000f
                                   * ResolveThrusterEfficiency(channel) * auxiliaryCapacityScale;
                        direction = -anchor.forward.normalized;
                    }
                }

                translationNozzleDirections[i] = direction;
                var origin = channel.ResolveWorldOrigin(transform);
                translationNozzleTorquePerNewton[i] = Vector3.Cross(
                    origin - FixedControlCenterWorld,
                    direction);
                translationNozzleCapacities[i] = capacity;
                translationNozzleOutputs[i] = 0f;
                translationNozzleLockedOutputs[i] = false;
            }

            AllocatePairedMainThrusters(requestedForce);
            for (var i = 0; i < channelCount; i++)
            {
                translationNozzleLockedOutputs[i] = thrusterChannels[i].Id.StartsWith(
                    "main.",
                    StringComparison.Ordinal);
            }

            MechTranslationAllocator.Solve(
                requestedForce,
                translationNozzleDirections,
                translationNozzleTorquePerNewton,
                translationNozzleCapacities,
                translationNozzleLockedOutputs,
                translationNozzleOutputs,
                translationAllocatorIterations,
                translationTorqueNeutralityWeight,
                translationTorqueReferenceArmMeters,
                out var achievedForce,
                out currentAllocationTorqueNewtonMeters);

            ApplyTranslationSymmetryConstraints(requestedForce);

            achievedForce = Vector3.zero;
            currentAllocationTorqueNewtonMeters = Vector3.zero;
            for (var i = 0; i < channelCount; i++)
            {
                var channelForce = translationNozzleDirections[i]
                                   * translationNozzleOutputs[i];
                thrusterChannels[i].SetForce(
                    channelForce,
                    translationNozzleCapacities[i]);
                achievedForce += channelForce;
                currentAllocationTorqueNewtonMeters += translationNozzleTorquePerNewton[i]
                                                      * translationNozzleOutputs[i];
            }

            return achievedForce;
        }

        private Vector3 ResolveMainVectorForceDirection(
            Vector3 requestedForce,
            float deltaTime)
        {
            var desiredLocalDirection = Vector3.forward;
            var localRequest = Quaternion.Inverse(LogicalRotation) * requestedForce;
            if (vectorThrustEnabled && localRequest.z > 0.001f)
            {
                var horizontalLength = Mathf.Sqrt(
                    localRequest.x * localRequest.x +
                    localRequest.z * localRequest.z);
                var requestedYaw = Mathf.Atan2(
                    localRequest.x,
                    Mathf.Max(0.001f, localRequest.z)) * Mathf.Rad2Deg;
                var requestedPitch = Mathf.Atan2(
                    localRequest.y,
                    Mathf.Max(0.001f, horizontalLength)) * Mathf.Rad2Deg;
                var clampedYaw = Mathf.Clamp(
                    requestedYaw,
                    -maximumVectorYawDegrees,
                    maximumVectorYawDegrees);
                var clampedPitch = Mathf.Clamp(
                    requestedPitch,
                    -maximumVectorPitchDegrees,
                    maximumVectorPitchDegrees);
                desiredLocalDirection =
                    Quaternion.Euler(-clampedPitch, clampedYaw, 0f) * Vector3.forward;
            }

            var maximumStepRadians = Mathf.Max(
                                         1f,
                                         mainVectorSlewRateDegreesPerSecond)
                                     * Mathf.Deg2Rad
                                     * Mathf.Max(0f, deltaTime);
            currentMainVectorDirectionLocal = Vector3.RotateTowards(
                currentMainVectorDirectionLocal.sqrMagnitude > 0.0001f
                    ? currentMainVectorDirectionLocal.normalized
                    : Vector3.forward,
                desiredLocalDirection.normalized,
                maximumStepRadians,
                0f).normalized;
            return LogicalRotation * currentMainVectorDirectionLocal;
        }

        private Vector3 AllocatePairedMainThrusters(Vector3 requestedForce)
        {
            var logicalForward = LogicalRotation * Vector3.forward;
            var requestedForwardForce = Mathf.Max(
                0f,
                Vector3.Dot(requestedForce, logicalForward));
            var availableForwardForce = 0f;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (!thrusterChannels[i].Id.StartsWith(
                        "main.",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                availableForwardForce += Mathf.Max(
                    0f,
                    Vector3.Dot(
                        translationNozzleDirections[i] * translationNozzleCapacities[i],
                        logicalForward));
            }

            var throttle = availableForwardForce > Mathf.Epsilon
                ? Mathf.Clamp01(requestedForwardForce / availableForwardForce)
                : 0f;
            var achievedForce = Vector3.zero;
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (!thrusterChannels[i].Id.StartsWith(
                        "main.",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                translationNozzleOutputs[i] = translationNozzleCapacities[i] * throttle;
                achievedForce += translationNozzleDirections[i]
                                 * translationNozzleOutputs[i];
            }

            return achievedForce;
        }

        private void ApplyTranslationSymmetryConstraints(Vector3 requestedForce)
        {
            if (requestedForce.sqrMagnitude <= Mathf.Epsilon)
            {
                return;
            }

            var localRequest = Quaternion.Inverse(LogicalRotation) * requestedForce;
            if (Mathf.Abs(localRequest.x) > Mathf.Max(100f, requestedForce.magnitude * 0.01f))
            {
                return;
            }

            var plume = GetComponent<MechMainThrusterPlume>();
            if (plume == null)
            {
                return;
            }

            const string prefix = "physical.translation.";
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                var id = thrusterChannels[i].Id;
                if (!id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var layoutId = id[prefix.Length..];
                if (!plume.TryGetMirroredNozzleId(layoutId, out var mirroredLayoutId))
                {
                    continue;
                }

                var mirroredId = prefix + mirroredLayoutId;
                if (string.CompareOrdinal(id, mirroredId) < 0)
                {
                    ApplyMirroredOutputPair(id, mirroredId);
                }
            }
        }

        private void ApplyMirroredOutputPair(string leftId, string rightId)
        {
            var leftIndex = FindThrusterChannelIndex(leftId);
            var rightIndex = FindThrusterChannelIndex(rightId);
            if (leftIndex < 0 || rightIndex < 0)
            {
                return;
            }

            var leftCapacity = translationNozzleCapacities[leftIndex];
            var rightCapacity = translationNozzleCapacities[rightIndex];
            if (leftCapacity <= Mathf.Epsilon || rightCapacity <= Mathf.Epsilon)
            {
                return;
            }

            var averageThrottle = 0.5f *
                                  (translationNozzleOutputs[leftIndex] / leftCapacity +
                                   translationNozzleOutputs[rightIndex] / rightCapacity);
            translationNozzleOutputs[leftIndex] = averageThrottle * leftCapacity;
            translationNozzleOutputs[rightIndex] = averageThrottle * rightCapacity;
        }

        private int FindThrusterChannelIndex(string id)
        {
            for (var i = 0; i < thrusterChannels.Count; i++)
            {
                if (string.Equals(thrusterChannels[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private void EnsureTranslationAllocatorBuffers(int requiredCount)
        {
            if (translationNozzleDirections.Length == requiredCount)
            {
                return;
            }

            translationNozzleDirections = new Vector3[requiredCount];
            translationNozzleTorquePerNewton = new Vector3[requiredCount];
            translationNozzleCapacities = new float[requiredCount];
            translationNozzleOutputs = new float[requiredCount];
            translationNozzleLockedOutputs = new bool[requiredCount];
        }

        private Vector3 ResolveAssembledCenterOfMassLocal(Vector3 fallbackWorldCenter)
        {
            runtimeState ??= GetComponent<MechRuntimeState>();
            if (runtimeState == null || runtimeState.AssembledParts.Count == 0)
            {
                return transform.InverseTransformPoint(fallbackWorldCenter);
            }

            var weightedWorldCenter = Vector3.zero;
            var totalMassKg = 0f;
            for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
            {
                var record = runtimeState.AssembledParts[i];
                var massKg = record?.part?.Stats != null
                    ? Mathf.Max(0f, record.part.Stats.massKg)
                    : 0f;
                if (massKg <= Mathf.Epsilon || record?.instance == null)
                {
                    continue;
                }

                var worldCenter = TryGetExclusivePartRendererBounds(record.instance, out var bounds)
                    ? bounds.center
                    : record.instance.transform.position;
                weightedWorldCenter += worldCenter * massKg;
                totalMassKg += massKg;
            }

            return totalMassKg > Mathf.Epsilon
                ? transform.InverseTransformPoint(weightedWorldCenter / totalMassKg)
                : transform.InverseTransformPoint(fallbackWorldCenter);
        }

        private static bool TryGetExclusivePartRendererBounds(GameObject partRoot, out Bounds bounds)
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

        private bool CanThrusterChannelProvideForce(
            MechThrusterChannelState channel,
            Vector3 requestedForce)
        {
            if (!channel.Id.StartsWith("physical.", StringComparison.Ordinal))
            {
                return true;
            }

            thrusterPlume ??= GetComponent<MechMainThrusterPlume>();
            return thrusterPlume != null &&
                   thrusterPlume.CanAuxiliaryNozzleProvideForce(
                       channel.Anchor,
                       requestedForce);
        }

        private float ResolveThrusterForceCapacity(
            MechThrusterRole role,
            float ratedGroupForceNewtons,
            bool compensatePairedSides)
        {
            var ratedMagnitude = Mathf.Max(0f, ratedGroupForceNewtons);
            if (ratedMagnitude <= Mathf.Epsilon)
            {
                return 0f;
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
                return 0f;
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

                switch (channel.Side)
                {
                    case MechThrusterSide.Left when leftCount > 0 && rightCount > 0:
                        leftCapacity += ratedMagnitude * 0.5f / leftCount * ResolveThrusterEfficiency(channel);
                        break;
                    case MechThrusterSide.Right when leftCount > 0 && rightCount > 0:
                        rightCapacity += ratedMagnitude * 0.5f / rightCount * ResolveThrusterEfficiency(channel);
                        break;
                    default:
                        centerCapacity += ratedMagnitude / channelCount * ResolveThrusterEfficiency(channel);
                        break;
                }
            }

            if (!compensatePairedSides || leftCount == 0 || rightCount == 0)
            {
                return leftCapacity + rightCapacity + centerCapacity;
            }

            var symmetricSideCapacity = Mathf.Min(leftCapacity, rightCapacity);
            var compensatedLeft = symmetricSideCapacity
                                  + Mathf.Max(0f, leftCapacity - rightCapacity)
                                  * damagedSideCompensationEfficiency;
            var compensatedRight = symmetricSideCapacity
                                   + Mathf.Max(0f, rightCapacity - leftCapacity)
                                   * damagedSideCompensationEfficiency;
            return compensatedLeft + compensatedRight + centerCapacity;
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
                var up = yawBasis * Vector3.up;
                var shoulderRequest = up * (localForce.y * shoulderFraction);
                var legRequest = up * (localForce.y * (1f - shoulderFraction));
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

        private Vector3 AllocateAvailableDirectionalForce(
            Vector3 requestedForce,
            Quaternion attitudeBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons,
            float upwardNewtons,
            float downwardNewtons)
        {
            if (requestedForce.sqrMagnitude <= Mathf.Epsilon)
            {
                return Vector3.zero;
            }

            var localForce = Quaternion.Inverse(attitudeBasis) * requestedForce;
            var allocatedForce = Vector3.zero;
            if (Mathf.Abs(localForce.z) > Mathf.Epsilon)
            {
                var role = localForce.z >= 0f
                    ? MechThrusterRole.Main
                    : MechThrusterRole.Reverse;
                var ratedCapacity = localForce.z >= 0f
                    ? mainNewtons
                    : reverseNewtons;
                var remainingCapacity = Mathf.Max(
                    0f,
                    ResolveThrusterForceCapacity(role, ratedCapacity, true) -
                    SumThrusterForce(role).magnitude);
                var request = attitudeBasis * new Vector3(
                    0f,
                    0f,
                    Mathf.Clamp(localForce.z, -remainingCapacity, remainingCapacity));
                allocatedForce += AllocateThrusterForce(
                    role,
                    request,
                    ratedCapacity,
                    true);
            }

            if (Mathf.Abs(localForce.x) > Mathf.Epsilon)
            {
                var role = localForce.x >= 0f
                    ? MechThrusterRole.StrafePositive
                    : MechThrusterRole.StrafeNegative;
                var remainingCapacity = Mathf.Max(
                    0f,
                    ResolveThrusterForceCapacity(role, lateralNewtons, false) -
                    SumThrusterForce(role).magnitude);
                var request = attitudeBasis * new Vector3(
                    Mathf.Clamp(localForce.x, -remainingCapacity, remainingCapacity),
                    0f,
                    0f);
                allocatedForce += AllocateThrusterForce(
                    role,
                    request,
                    lateralNewtons,
                    false);
            }

            if (localForce.y > Mathf.Epsilon)
            {
                var shoulderRated = upwardNewtons * Mathf.Clamp01(shoulderLiftFraction);
                var legRated = upwardNewtons * (1f - Mathf.Clamp01(shoulderLiftFraction));
                var shoulderRemaining = Mathf.Max(
                    0f,
                    ResolveThrusterForceCapacity(
                        MechThrusterRole.ShoulderLift,
                        shoulderRated,
                        true) -
                    SumThrusterForce(MechThrusterRole.ShoulderLift).magnitude);
                var legRemaining = Mathf.Max(
                    0f,
                    ResolveThrusterForceCapacity(
                        MechThrusterRole.LegLift,
                        legRated,
                        true) -
                    SumThrusterForce(MechThrusterRole.LegLift).magnitude);
                var totalRemaining = shoulderRemaining + legRemaining;
                var requestedUp = Mathf.Min(localForce.y, totalRemaining);
                if (requestedUp > Mathf.Epsilon && totalRemaining > Mathf.Epsilon)
                {
                    var up = attitudeBasis * Vector3.up;
                    var shoulderRequest = up * requestedUp
                                          * (shoulderRemaining / totalRemaining);
                    var legRequest = up * requestedUp
                                     * (legRemaining / totalRemaining);
                    allocatedForce += AllocateThrusterForce(
                        MechThrusterRole.ShoulderLift,
                        shoulderRequest,
                        shoulderRated,
                        true);
                    allocatedForce += AllocateThrusterForce(
                        MechThrusterRole.LegLift,
                        legRequest,
                        legRated,
                        true);
                }
            }
            else if (localForce.y < -Mathf.Epsilon)
            {
                var remainingCapacity = Mathf.Max(
                    0f,
                    ResolveThrusterForceCapacity(
                        MechThrusterRole.Descent,
                        downwardNewtons,
                        true) -
                    SumThrusterForce(MechThrusterRole.Descent).magnitude);
                var requestMagnitude = Mathf.Min(-localForce.y, remainingCapacity);
                allocatedForce += AllocateThrusterForce(
                    MechThrusterRole.Descent,
                    -(attitudeBasis * Vector3.up) * requestMagnitude,
                    downwardNewtons,
                    true);
            }

            return allocatedForce;
        }

        private Vector3 AllocateDirectionPreservingForce(
            Vector3 requestedForce,
            Quaternion yawBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons,
            float upwardNewtons,
            float downwardNewtons)
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
                reverseNewtons,
                upwardNewtons,
                downwardNewtons);
            return AllocateDirectionalReserveForce(
                direction * Mathf.Min(requestedForce.magnitude, capacity),
                yawBasis);
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

        private void AddPhysicalAuxiliaryThrusterChannels(
            IReadOnlyDictionary<string, float> previousHealth)
        {
            var plume = GetComponent<MechMainThrusterPlume>();
            if (plume == null)
            {
                return;
            }

            plume.CollectAuxiliaryNozzleBindings(physicalAuxiliaryNozzles);
            for (var nozzleIndex = 0;
                 nozzleIndex < physicalAuxiliaryNozzles.Count;
                 nozzleIndex++)
            {
                var nozzle = physicalAuxiliaryNozzles[nozzleIndex];
                var localPosition = transform.InverseTransformPoint(nozzle.Anchor.position);
                var side = Mathf.Abs(localPosition.x) <= 0.01f
                    ? MechThrusterSide.Center
                    : localPosition.x < 0f
                        ? MechThrusterSide.Left
                        : MechThrusterSide.Right;

                var id = $"physical.translation.{nozzle.Id}";
                AddThrusterChannel(
                    id,
                    MechThrusterRole.Translation,
                    side,
                    nozzle.Anchor,
                    localPosition,
                    false,
                    previousHealth);
            }
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

            var candidates = GetComponentsInChildren<Transform>(true);
            var leftProjection = float.PositiveInfinity;
            var rightProjection = float.NegativeInfinity;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (!candidate.name.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase) ||
                    !IsUnderBoosterVisual(candidate))
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

        private static bool IsUnderBoosterVisual(Transform candidate)
        {
            for (var current = candidate; current != null; current = current.parent)
            {
                var assembledPart = current.GetComponent<MechAssembledPart>();
                if (assembledPart != null)
                {
                    return assembledPart.Slot == MechPartSlot.Booster;
                }

                if (current.name.StartsWith("Booster_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
            out float reverseNewtons,
            out float upwardNewtons,
            out float downwardNewtons)
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var stats = useLoadoutPropulsionStats && runtimeState != null
                ? runtimeState.AggregatedStats
                : null;
            if (stats != null)
            {
                mainNewtons = Mathf.Max(0f, stats.mainThrustKn) * 1000f;
                lateralNewtons = Mathf.Max(0f, stats.lateralThrustKn) * 1000f;
                reverseNewtons = Mathf.Max(0f, stats.reverseThrustKn) * 1000f;
                upwardNewtons = Mathf.Max(0f, stats.upwardThrustKn) * 1000f;
                downwardNewtons = Mathf.Max(0f, stats.downwardThrustKn) * 1000f;
                return;
            }

            mainNewtons = Mathf.Max(0f, fallbackMainThrustKn) * 1000f;
            lateralNewtons = Mathf.Max(0f, fallbackLateralThrustKn) * 1000f;
            reverseNewtons = Mathf.Max(0f, fallbackReverseThrustKn) * 1000f;
            upwardNewtons = Mathf.Max(0f, verticalThrustKn) * 1000f;
            downwardNewtons = Mathf.Max(
                0f,
                verticalThrustKn * Mathf.Clamp01(descentThrustFraction)) * 1000f;
        }

        private float ResolveOverloadAccelerationScale()
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var stats = runtimeState != null ? runtimeState.AggregatedStats : null;
            if (stats == null || stats.supportedMassKg <= 0f)
            {
                return 1f;
            }

            return CalculateOverloadAccelerationScale(
                ResolveMassKg(),
                stats.supportedMassKg);
        }

        public static float CalculateOverloadAccelerationScale(
            float totalMassKg,
            float supportedMassKg)
        {
            if (totalMassKg <= 0f || supportedMassKg <= 0f ||
                totalMassKg <= supportedMassKg)
            {
                return 1f;
            }

            var loadRatio = totalMassKg / supportedMassKg;
            return Mathf.Clamp(
                1f / (loadRatio * loadRatio * loadRatio),
                0.2f,
                1f);
        }

		private float ResolveMaximumYawRateDegrees()
		{
			if (runtimeState == null)
			{
				runtimeState = GetComponent<MechRuntimeState>();
			}

			var stats = runtimeState != null ? runtimeState.AggregatedStats : null;
			return stats != null
				? Mathf.Max(0f, stats.rcsTurnRateDegreesPerSecond)
				: Mathf.Max(0f, maximumYawRateDegrees);
		}

		private float ResolveRcsThrustKn()
		{
			if (runtimeState == null)
			{
				runtimeState = GetComponent<MechRuntimeState>();
			}

			var stats = runtimeState != null ? runtimeState.AggregatedStats : null;
			return stats != null
				? Mathf.Max(0f, stats.rcsThrustKn)
				: Mathf.Max(0f, shoulderRcsThrustKn + legRcsThrustKn);
		}

        private Vector3 ResolveMainThrustDirection(Quaternion yawBasis, bool grounded)
        {
            return yawBasis * Vector3.forward;
        }

        private void ResolveLiftThrusterForces(
            in MechPlayerInputFrame frame,
            Quaternion yawBasis,
            Vector3 mainThrustDirection,
            Vector3 controlRight,
            Vector3 controlForward,
            bool grounded,
            bool quickBoostApplying,
            float upwardNewtons,
            float downwardNewtons,
            out Vector3 shoulderForce,
            out Vector3 legForce,
            out Vector3 descentForce)
        {
            shoulderForce = Vector3.zero;
            legForce = Vector3.zero;
            descentForce = Vector3.zero;

            var totalLiftNewtons = Mathf.Max(0f, upwardNewtons);
            if (totalLiftNewtons <= Mathf.Epsilon)
            {
                return;
            }

            // Ctrl 下降是主动向下施力，不直接改写高度或垂直速度。
            // Space 与 Ctrl 同时按下时互相抵消，避免相反的喷口组同时满推。
            var verticalInput = (frame.RiseHeld ? 1f : 0f) - (frame.DescendHeld ? 1f : 0f);
            if (verticalInput < -Mathf.Epsilon)
            {
                var descentNewtons = Mathf.Max(0f, downwardNewtons) * -verticalInput;
                descentForce = -ReferenceUp * descentNewtons;
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
            var longitudinalCommand = Mathf.Clamp(frame.MoveLocal.y, -1f, 1f);
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

                // Horizontal coordination may use vectored lift nozzles, but it must not create
                // an uncommanded climb. Limit the upward group to what the descent group can trim.
                var gravitySupportNewtons = currentMassKg * gravityAcceleration;
                var descentCapacityNewtons = Mathf.Max(0f, downwardNewtons);
                var balancedLiftLimit = (gravitySupportNewtons + descentCapacityNewtons)
                                        / totalLiftNewtons;
                liftCommand = Mathf.Min(liftCommand, Mathf.Clamp01(balancedLiftLimit));
            }

            if (liftCommand <= Mathf.Epsilon)
            {
                return;
            }

            var shoulderDirection = ReferenceUp;
            if (vectorThrustEnabled && horizontalIntent.sqrMagnitude > 0.0001f)
            {
                shoulderDirection = TiltUpToward(
                    horizontalIntent,
                    ReferenceUp,
                    shoulderLiftVectorDegrees);
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
                ? TiltUpToward(horizontalIntent, ReferenceUp, legDeflection)
                : ReferenceUp;

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
                desiredVerticalForce
                - Vector3.Dot(shoulderForce, ReferenceUp)
                - Vector3.Dot(legForce, ReferenceUp));
            verticalDeficit = AddVerticalCompensation(
                ref shoulderForce,
                shoulderDirection,
                ReferenceUp,
                shoulderBaseMagnitude * liftCompensationReserve,
                verticalDeficit);
            AddVerticalCompensation(
                ref legForce,
                legDirection,
                ReferenceUp,
                legBaseMagnitude * liftCompensationReserve,
                verticalDeficit);

            // With no explicit climb command, automatically cancel surplus upward force. Gravity
            // remains external, so retaining mass*g of net upward force gives neutral altitude.
            if (!grounded && verticalInput <= Mathf.Epsilon)
            {
                var gravitySupportNewtons = currentMassKg * gravityAcceleration;
                var upwardForceNewtons = Mathf.Max(
                    0f,
                    Vector3.Dot(shoulderForce + legForce, ReferenceUp));
                var trimNewtons = Mathf.Clamp(
                    upwardForceNewtons - gravitySupportNewtons,
                    0f,
                    Mathf.Max(0f, downwardNewtons));
                descentForce = -ReferenceUp * trimNewtons;
            }
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
                var horizontalVelocity = Vector3.ProjectOnPlane(velocity, ReferenceUp);
                return horizontalVelocity.sqrMagnitude > 0.0001f
                    ? -horizontalVelocity.normalized
                    : Vector3.zero;
            }

            if (quickBoostApplying && quickBoostDirection.sqrMagnitude > 0.0001f)
            {
                return Vector3.ProjectOnPlane(quickBoostDirection, ReferenceUp).normalized;
            }

            if (AssaultBoostActive)
            {
                return Vector3.ProjectOnPlane(mainThrustDirection, ReferenceUp).normalized;
            }

            var longitudinalCommand = Mathf.Clamp(frame.MoveLocal.y, -1f, 1f);
            var inputDirection = controlRight * frame.MoveLocal.x
                                 + controlForward * longitudinalCommand;
            return inputDirection.sqrMagnitude > 0.0001f
                ? inputDirection.normalized
                : Vector3.zero;
        }

        private static Vector3 TiltUpToward(
            Vector3 horizontalDirection,
            Vector3 up,
            float degrees)
        {
            up = up.sqrMagnitude > 0.0001f ? up.normalized : Vector3.up;
            var horizontal = Vector3.ProjectOnPlane(horizontalDirection, up);
            if (horizontal.sqrMagnitude <= 0.0001f || degrees <= 0f)
            {
                return up;
            }

            horizontal.Normalize();
            var axis = Vector3.Cross(up, horizontal);
            return (Quaternion.AngleAxis(Mathf.Clamp(degrees, 0f, 89f), axis) * up)
                .normalized;
        }

        private static float AddVerticalCompensation(
            ref Vector3 force,
            Vector3 direction,
            Vector3 up,
            float reserveMagnitude,
            float verticalDeficit)
        {
            var verticalDirection = Vector3.Dot(direction, up);
            if (verticalDeficit <= Mathf.Epsilon || reserveMagnitude <= Mathf.Epsilon ||
                verticalDirection <= 0.0001f)
            {
                return verticalDeficit;
            }

            var addedMagnitude = Mathf.Min(
                reserveMagnitude,
                verticalDeficit / verticalDirection);
            force += direction * addedMagnitude;
            return Mathf.Max(
                0f,
                verticalDeficit - addedMagnitude * verticalDirection);
        }

        private static float ResolveDirectionalForceCapacity(
            Vector3 worldDirection,
            Quaternion yawBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons,
            float upwardNewtons,
            float downwardNewtons)
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
            var vertical = localDirection.y >= 0f
                ? localDirection.y * upwardNewtons
                : -localDirection.y * downwardNewtons;
            return Mathf.Max(0f, longitudinal + lateral + vertical);
        }

        private static float ResolveDirectionPreservingForceCapacity(
            Vector3 worldDirection,
            Quaternion yawBasis,
            float mainNewtons,
            float lateralNewtons,
            float reverseNewtons,
            float upwardNewtons,
            float downwardNewtons)
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

            if (Mathf.Abs(localDirection.y) > 0.0001f)
            {
                var verticalCapacity = localDirection.y >= 0f
                    ? upwardNewtons
                    : downwardNewtons;
                capacity = Mathf.Min(
                    capacity,
                    verticalCapacity / Mathf.Abs(localDirection.y));
            }

            return float.IsPositiveInfinity(capacity) ? 0f : Mathf.Max(0f, capacity);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) &&
                   float.IsFinite(value.y) &&
                   float.IsFinite(value.z);
        }

        private void ApplyVisualRotation()
        {
            transform.rotation =
                LogicalRotation *
                Quaternion.AngleAxis(modelForwardYawOffset, Vector3.up);
        }

        private void RefreshGroundHeight(Vector3 position)
        {
            hasSampledGround = TrySampleGroundSurface(
                position,
                ReferenceUp,
                out sampledGroundPoint,
                out sampledGroundNormal);
            if (!hasSampledGround)
            {
                sampledGroundPoint = position;
            }
        }

        private bool TrySampleGroundSurface(
            Vector3 position,
            Vector3 referenceUp,
            out Vector3 surfacePoint,
            out Vector3 surfaceNormal)
        {
            var up = referenceUp.sqrMagnitude > 0.0001f
                ? referenceUp.normalized
                : Vector3.up;
            var origin = position + up * 12f;
            var hitCount = Physics.RaycastNonAlloc(
                origin,
                -up,
                groundHits,
                groundProbeDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            var foundGround = false;
            var nearestDistance = float.PositiveInfinity;
            surfacePoint = position;
            surfaceNormal = up;
            for (var i = 0; i < hitCount; i++)
            {
                var hit = groundHits[i];
                if (hit.collider == null ||
                    Vector3.Dot(hit.normal, up) < minimumGroundNormalY)
                {
                    continue;
                }

                var hitTransform = hit.collider.transform;
                if (hitTransform == transform || hitTransform.IsChildOf(transform))
                {
                    continue;
                }

                if (!foundGround || hit.distance < nearestDistance)
                {
                    foundGround = true;
                    nearestDistance = hit.distance;
                    surfacePoint = hit.point;
                    surfaceNormal = hit.normal;
                }
            }

            return foundGround;
        }

        private void EnsureManeuverEnergyInitialized()
        {
            if (maneuverEnergyInitialized)
            {
                return;
            }

            maximumManeuverEnergy = Mathf.Max(1f, maximumManeuverEnergy);
            quickBoostEnergyCost = Mathf.Clamp(
                quickBoostEnergyCost,
                0f,
                maximumManeuverEnergy);
            currentManeuverEnergy = maximumManeuverEnergy;
            maneuverEnergyRechargeDelayRemaining = 0f;
            maneuverEnergyDepleted = false;
            maneuverEnergyInitialized = true;
        }

        private void RefreshLoadoutEnergyConfiguration()
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var loadout = runtimeState != null ? runtimeState.CurrentLoadout : null;
            if (loadout == null)
            {
                return;
            }

            loadout.TryGetPart(MechPartSlot.Generator, out var generator);
            loadout.TryGetPart(MechPartSlot.Booster, out var booster);
            if (ReferenceEquals(loadout, appliedEnergyLoadout) &&
                ReferenceEquals(generator, appliedEnergyGenerator) &&
                ReferenceEquals(booster, appliedEnergyBooster))
            {
                return;
            }

            var normalizedEnergy = maneuverEnergyInitialized
                ? Mathf.Clamp01(currentManeuverEnergy / Mathf.Max(0.001f, maximumManeuverEnergy))
                : 1f;
            var generatorPerformance = generator?.GeneratorPerformance;
            if (generatorPerformance != null && generatorPerformance.HasData)
            {
                maximumManeuverEnergy = Mathf.Max(0.001f, generatorPerformance.energyStorageCapacityMj);
                maneuverEnergyRechargePerSecond = Mathf.Max(0f, generatorPerformance.energyRechargePowerKw) / 1000f;
                maneuverEnergyRechargeDelay = Mathf.Max(0f, generatorPerformance.energyRechargeDelayS);
                maneuverEnergyRestartFraction = Mathf.Clamp01(generatorPerformance.depletedRestartFraction);
                if (maneuverEnergyInitialized)
                {
                    currentManeuverEnergy = maximumManeuverEnergy * normalizedEnergy;
                }
            }

            var boosterPerformance = booster?.BoosterPerformance;
            if (boosterPerformance != null && boosterPerformance.HasData)
            {
                assaultBoostThrustKn = Mathf.Max(0f, boosterPerformance.assaultBoostThrustKn);
                quickBoostThrustKn = Mathf.Max(0f, boosterPerformance.quickBoostThrustKn);
                quickBoostDuration = Mathf.Max(0.01f, boosterPerformance.quickBoostDurationS);
                maximumVectorPitchDegrees = Mathf.Clamp(
                    boosterPerformance.verticalVectorLimitDeg,
                    0f,
                    89f);
                quickBoostEnergyCost = Mathf.Clamp(
                    boosterPerformance.quickBoostPowerDrawKw * quickBoostDuration / 1000f,
                    0f,
                    maximumManeuverEnergy);
            }

            appliedEnergyLoadout = loadout;
            appliedEnergyGenerator = generator;
            appliedEnergyBooster = booster;
        }


        private void UpdateManeuverEnergyRecovery(float deltaTime)
        {
            var step = Mathf.Max(0f, deltaTime);
            if (maneuverEnergyRechargeDelayRemaining > 0f)
            {
                maneuverEnergyRechargeDelayRemaining = Mathf.Max(
                    0f,
                    maneuverEnergyRechargeDelayRemaining - step);
                return;
            }

            currentManeuverEnergy = Mathf.Min(
                maximumManeuverEnergy,
                currentManeuverEnergy +
                maneuverEnergyRechargePerSecond * (GetComponent<MechPartDamageController>()?.RoleEfficiency(MechPartSlot.Generator) ?? 1f) * step);
            if (maneuverEnergyDepleted &&
                currentManeuverEnergy >=
                maximumManeuverEnergy * maneuverEnergyRestartFraction)
            {
                maneuverEnergyDepleted = false;
            }
        }

        private float ResolveMovementEnergyDrainRate(
            in MechPlayerInputFrame frame,
            float forwardCommand)
        {
            // Sustained translation does not spend the burst EN reserve, even at zero EN.
            if (!AssaultBoostActive) return 0f;

            // Autonomous input's digital axes are animation/takeoff cues, not
            // extra full-throttle thrusters to bill on top of the world request.
            if (frame.HasAutonomousFlightCommand) forwardCommand = 0f;
            var lateralCommand = frame.HasAutonomousFlightCommand ? 0f : Mathf.Abs(frame.MoveLocal.x);
            var verticalCommand = frame.HasAutonomousFlightCommand ? 0f : Mathf.Abs(
                (frame.RiseHeld ? 1f : 0f) -
                (frame.DescendHeld ? 1f : 0f));
            var automaticAcceleration = frame.HasAutonomousFlightCommand
                ? frame.AutonomousAccelerationWorld
                : trajectorySyncCommandActive ? trajectorySyncAccelerationCommand : Vector3.zero;
            var automaticDemand = IsFinite(automaticAcceleration)
                ? Mathf.Clamp01(automaticAcceleration.magnitude / 20f) : 0f;
            var brakingDemand = braking &&
                                (!isGrounded || HorizontalSpeed > groundStopSpeed)
                ? 1f
                : 0f;
            if (!TryResolveEquippedPropulsionPower(
                    out var boosterPowerKw,
                    out var assaultBoostPowerKw,
                    out var armLateralPowerKw,
                    out var coreReversePowerKw,
                    out var coreDownwardPowerKw,
                    out var legPrimaryPowerKw,
                    out var legLateralPowerKw))
            {
                var manualDemand = Mathf.Clamp01(Mathf.Max(
                    lateralCommand,
                    Mathf.Abs(forwardCommand),
                    verticalCommand));
                var demand = Mathf.Max(manualDemand, automaticDemand, brakingDemand);
                var fallbackDrain = movementEnergyDrainPerSecond * demand;
                return AssaultBoostActive
                    ? Mathf.Max(fallbackDrain, assaultBoostEnergyDrainPerSecond)
                    : fallbackDrain;
            }

            var forwardDemand = Mathf.Max(0f, forwardCommand);
            var reverseDemand = Mathf.Max(0f, -forwardCommand);
            var lateralDemand = lateralCommand;
            var riseDemand = !frame.HasAutonomousFlightCommand && frame.RiseHeld ? 1f : 0f;
            var descendDemand = !frame.HasAutonomousFlightCommand && frame.DescendHeld ? 1f : 0f;

            // A vectored nozzle cannot spend its full rated power independently on
            // multiple axes. Each source therefore uses its strongest simultaneous demand.
            var legPrimaryDemand = Mathf.Max(forwardDemand, reverseDemand, riseDemand);
            var regularPowerKw = boosterPowerKw * forwardDemand +
                                 coreReversePowerKw * reverseDemand +
                                 coreDownwardPowerKw * descendDemand +
                                 armLateralPowerKw * lateralDemand +
                                 legLateralPowerKw * lateralDemand +
                                 legPrimaryPowerKw * legPrimaryDemand;
            var installedPropulsionPowerKw = boosterPowerKw + coreReversePowerKw +
                                             coreDownwardPowerKw +
                                             armLateralPowerKw + legLateralPowerKw +
                                             legPrimaryPowerKw;
            regularPowerKw = Mathf.Max(
                regularPowerKw,
                installedPropulsionPowerKw * Mathf.Max(automaticDemand, brakingDemand));
            if (AssaultBoostActive)
            {
                regularPowerKw = Mathf.Max(regularPowerKw, assaultBoostPowerKw);
            }
            return regularPowerKw / 1000f;
        }

        private bool TryResolveEquippedPropulsionPower(
            out float boosterPowerKw,
            out float assaultBoostPowerKw,
            out float armLateralPowerKw,
            out float coreReversePowerKw,
            out float coreDownwardPowerKw,
            out float legPrimaryPowerKw,
            out float legLateralPowerKw)
        {
            boosterPowerKw = 0f;
            assaultBoostPowerKw = 0f;
            armLateralPowerKw = 0f;
            coreReversePowerKw = 0f;
            coreDownwardPowerKw = 0f;
            legPrimaryPowerKw = 0f;
            legLateralPowerKw = 0f;

            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }
            var loadout = runtimeState != null ? runtimeState.CurrentLoadout : null;
            if (loadout == null)
            {
                return false;
            }

            if (loadout.TryGetPart(MechPartSlot.Booster, out var booster))
            {
                boosterPowerKw = Mathf.Max(0f, booster.Stats.peakPowerDrawMw) * 1000f;
                assaultBoostPowerKw = Mathf.Max(
                    boosterPowerKw,
                    booster.BoosterPerformance?.assaultBoostPowerDrawKw ?? 0f);
            }
            if (loadout.TryGetPart(MechPartSlot.Arms, out var arms))
            {
                armLateralPowerKw = Mathf.Max(0f, arms.ArmPerformance?.LateralPowerKw ?? 0f);
            }
            if (loadout.TryGetPart(MechPartSlot.Core, out var core))
            {
                coreReversePowerKw = Mathf.Max(0f, core.CorePerformance?.ReversePowerKw ?? 0f);
                coreDownwardPowerKw = Mathf.Max(0f, core.CorePerformance?.DownwardPowerKw ?? 0f);
            }
            if (loadout.TryGetPart(MechPartSlot.Legs, out var legs))
            {
                legPrimaryPowerKw = Mathf.Max(0f, legs.LegPerformance?.PrimaryPowerKw ?? 0f);
                legLateralPowerKw = Mathf.Max(0f, legs.LegPerformance?.LateralPowerKw ?? 0f);
            }

            return boosterPowerKw > 0f || armLateralPowerKw > 0f ||
                   coreReversePowerKw > 0f || coreDownwardPowerKw > 0f ||
                   legPrimaryPowerKw > 0f ||
                   legLateralPowerKw > 0f;
        }

        private float ConsumeMovementEnergy(float drainPerSecond, float deltaTime)
        {
            var requestedEnergy = Mathf.Max(0f, drainPerSecond) *
                                  Mathf.Max(0f, deltaTime);
            if (requestedEnergy <= Mathf.Epsilon)
            {
                return 1f;
            }

            if (maneuverEnergyDepleted || currentManeuverEnergy <= 0f)
            {
                assaultBoostActive = false;
                return 0f;
            }

            var availableFraction = Mathf.Clamp01(
                currentManeuverEnergy / requestedEnergy);
            currentManeuverEnergy = Mathf.Max(
                0f,
                currentManeuverEnergy - requestedEnergy);
            maneuverEnergyRechargeDelayRemaining =
                maneuverEnergyRechargeDelay;
            if (currentManeuverEnergy <= 0.0001f)
            {
                currentManeuverEnergy = 0f;
                maneuverEnergyDepleted = true;
                assaultBoostActive = false;
            }

            return availableFraction;
        }

        public bool TrySpendEquipmentEnergy(float energyMj)
        {
            if (float.IsNaN(energyMj) || float.IsInfinity(energyMj) || energyMj < 0) return false;
            float available = CurrentManeuverEnergy;
            if (available + 0.00001f < energyMj) return false;
            currentManeuverEnergy = Mathf.Max(0, available - energyMj);
            if (energyMj > 0) maneuverEnergyRechargeDelayRemaining = maneuverEnergyRechargeDelay;
            if (currentManeuverEnergy <= 0.0001f) { maneuverEnergyDepleted = true; assaultBoostActive = false; }
            return true;
        }

        private bool TrySpendQuickBoostEnergy()
        {
            EnsureManeuverEnergyInitialized();
            if (!CanQuickBoost)
            {
                return false;
            }

            currentManeuverEnergy = Mathf.Max(
                0f,
                currentManeuverEnergy - quickBoostEnergyCost);
            maneuverEnergyRechargeDelayRemaining =
                maneuverEnergyRechargeDelay;
            if (currentManeuverEnergy <= 0.0001f)
            {
                currentManeuverEnergy = 0f;
                maneuverEnergyDepleted = true;
                assaultBoostActive = false;
            }

            return true;
        }

        /// <summary>消耗一次能量并启动固定方向的 QB 高加速度窗口。</summary>
        public bool BeginQuickBoost(Vector2 moveLocal)
        {
            if (!engineAvailable || !CanQuickBoost)
            {
                return false;
            }

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
                out var reverseNewtons,
                out var upwardNewtons,
                out var downwardNewtons);
            var directionalCapacity = ResolveDirectionalForceCapacity(
                quickBoostDirection,
                yawBasis,
                mainNewtons,
                lateralNewtons,
                reverseNewtons,
                upwardNewtons,
                downwardNewtons);
            if (quickBoostThrustKn <= Mathf.Epsilon ||
                mainNewtons <= Mathf.Epsilon ||
                directionalCapacity <= Mathf.Epsilon)
            {
                return false;
            }

            if (!TrySpendQuickBoostEnergy())
            {
                return false;
            }

            quickBoostRemaining = quickBoostDuration;
            quickBoostRecoveryRemaining = quickBoostDuration + ResolveQuickBoostRecoverySeconds();
            var directionalScale = directionalCapacity / mainNewtons;
            quickBoostForceNewtons = quickBoostThrustKn * 1000f
                                     * Mathf.Max(0f, directionalScale)
                                     * ResolveOverloadAccelerationScale();
            return true;
        }

        private float ResolveQuickBoostRecoverySeconds()
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var booster = runtimeState != null
                ? runtimeState.AggregatedStats?.boosterPerformance
                : null;
            return booster != null ? Mathf.Max(0f, booster.quickBoostRecoveryS) : 0f;
        }
    }
}
