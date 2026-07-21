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
        [Tooltip("垂直推力中由肩部上推喷口承担的比例；其余由双腿/脚部喷口承担。")]
        [Range(0f, 1f)] [SerializeField] private float shoulderLiftFraction = 0.4f;
        [Tooltip("开启矢量推力时，肩部上推喷口相对世界向上允许的最大偏转角。")]
        [Range(0f, 45f)] [SerializeField] private float shoulderLiftVectorDegrees = 12f;
        [Tooltip("爬升或 AB 时，腿部姿态可以把脚部上推力偏向机动方向的最大角度。")]
        [Range(0f, 60f)] [SerializeField] private float legClimbDeflectionDegrees = 24f;
        [Tooltip("QB 时腿部姿态可以把脚部上推力偏向 QB 方向的最大角度。")]
        [Range(0f, 75f)] [SerializeField] private float legQuickBoostDeflectionDegrees = 42f;
        [Tooltip("主动刹车时腿部姿态可以把脚部上推力偏向反速度方向的最大角度。")]
        [Range(0f, 75f)] [SerializeField] private float legBrakeDeflectionDegrees = 38f;
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
        [Tooltip("Ctrl 制动调用对应方向喷口时的推力倍率。1 表示不超出正常主推/侧推/反推能力。")]
        [SerializeField] private float activeBrakingThrustMultiplier = 1f;

        [Header("朝向")]
        [Tooltip("鼠标水平灵敏度（度/像素）。Yaw 唯一来源是鼠标，A/D 只平移。")]
        [SerializeField] private float yawSensitivity = 0.15f;
        [Tooltip("模型视觉前方相对逻辑前方(+Z)的 Yaw 修正（度）。校准模型轴用，禁止用镜头摆位掩盖。")]
        [SerializeField] private float modelForwardYawOffset;

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
        private float aimPitchDegrees;
        private Vector3 velocity;
        private float quickBoostRemaining;
        private Vector3 quickBoostDirection;
        private bool assaultBoostActive;
        private bool braking;
        private float mainThrottle;
        private bool flightEnvelopeLimiting;
        private bool flightEnvelopeOverrideActive;
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
        private float quickBoostForceNewtons;
        private readonly RaycastHit[] groundHits = new RaycastHit[32];

        public float YawDegrees => yawDegrees;

        public float LogicalYawDegrees => yawDegrees;

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

        public bool VectorThrustEnabled => vectorThrustEnabled;

        /// <summary>当前帧经速度包线处理后的持续推进加速度，不含重力与短时 QB。</summary>
        public Vector3 CurrentControlAcceleration => currentControlAcceleration;

        /// <summary>当前 QB 窗口的世界空间加速度；窗口结束后返回零。</summary>
        public Vector3 CurrentQuickBoostAcceleration => quickBoostRemaining > 0f
            ? quickBoostDirection * (quickBoostForceNewtons / ResolveMassKg())
            : Vector3.zero;

        public float CurrentQuickBoostRemaining => quickBoostRemaining;

        public float AirCoastingDragAcceleration => airCoastingDragAcceleration;

        public bool FlightEnvelopeLimiting => flightEnvelopeLimiting;

        public bool FlightEnvelopeOverrideActive => flightEnvelopeOverrideActive;

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
            sampledGroundY = groundY;
            RefreshGroundHeight(transform.position);
            isGrounded = transform.position.y <= sampledGroundY + groundedEpsilon;
        }

        public void ConfigureModelForwardYawOffset(float yawOffsetDegrees)
        {
            modelForwardYawOffset = Mathf.Repeat(yawOffsetDegrees, 360f);
            yawDegrees = 0f;
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
            currentMassKg = ResolveMassKg();
            currentControlForceNewtons = Vector3.zero;
            currentControlAcceleration = Vector3.zero;
            currentShoulderLiftForceNewtons = Vector3.zero;
            currentLegLiftForceNewtons = Vector3.zero;

            // Yaw：只来自鼠标水平；Roll 恒 0（LogicalRotation 只含 Yaw，结构性保证）。
            yawDegrees = Mathf.Repeat(yawDegrees + frame.MouseDelta.x * yawSensitivity, 360f);

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
            mainThrottle = Mathf.Clamp01(frame.MainThrottle);

            // 节流是持久的前向推力命令。W/S 在该命令上临时加减，A/D 仍是侧向推进。
            var forwardCommand = Mathf.Clamp(frame.MoveLocal.y + mainThrottle, -1f, 1f);
            ResolveThrusterForces(
                out var mainThrustNewtons,
                out var lateralThrustNewtons,
                out var reverseThrustNewtons);

            // 每一组喷口独立贡献力。斜向输入会同时点燃主推和侧推，不再把合力归一化。
            var propulsionForce = right * (frame.MoveLocal.x * lateralThrustNewtons);
            if (forwardCommand >= 0f)
            {
                propulsionForce += mainThrustDirection * (forwardCommand * mainThrustNewtons);
            }
            else
            {
                propulsionForce -= bodyForward * (-forwardCommand * reverseThrustNewtons);
            }

            var quickBoostApplying = quickBoostStep > 0f;
            ResolveLiftThrusterForces(
                in frame,
                yawBasis,
                mainThrustDirection,
                grounded,
                quickBoostApplying,
                out currentShoulderLiftForceNewtons,
                out currentLegLiftForceNewtons);
            propulsionForce += currentShoulderLiftForceNewtons + currentLegLiftForceNewtons;

            if (AssaultBoostActive)
            {
                propulsionForce += mainThrustDirection * (assaultBoostThrustKn * 1000f);
            }

            var hasPropulsionCommand = propulsionForce.sqrMagnitude > 0.0001f;
            flightEnvelopeOverrideActive = frame.FlightEnvelopeOverrideHeld
                                           || AssaultBoostActive
                                           || quickBoostApplying;
            flightEnvelopeLimiting = false;

            if (!braking && hasPropulsionCommand)
            {
                var propulsionAcceleration = propulsionForce / currentMassKg;
                propulsionAcceleration = ApplyFlightEnvelope(
                    propulsionAcceleration, flightEnvelopeOverrideActive);
                currentControlForceNewtons = propulsionAcceleration * currentMassKg;
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
                    velocity += Vector3.ClampMagnitude(
                        -lateralVelocity,
                        assistAcceleration * deltaTime);
                }
            }

            if (quickBoostApplying && quickBoostDirection.sqrMagnitude > 0.0001f)
            {
                velocity += quickBoostDirection
                            * (quickBoostForceNewtons / currentMassKg * quickBoostStep);
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
                var brakingForce = ResolveDirectionalForceCapacity(
                    brakingDirection,
                    yawBasis,
                    mainThrustNewtons,
                    lateralThrustNewtons,
                    reverseThrustNewtons);
                var liftManeuverForce = Vector3.ProjectOnPlane(
                    currentShoulderLiftForceNewtons + currentLegLiftForceNewtons,
                    Vector3.up);
                brakingForce += Mathf.Max(0f, Vector3.Dot(liftManeuverForce, brakingDirection));
                horizontal = Vector3.MoveTowards(
                    horizontal,
                    Vector3.zero,
                    brakingForce / currentMassKg * activeBrakingThrustMultiplier * deltaTime);
                if (horizontal.magnitude <= groundStopSpeed)
                {
                    horizontal = Vector3.zero;
                }

                velocity = new Vector3(horizontal.x, 0f, horizontal.z);
                currentControlForceNewtons = brakingDirection * brakingForce
                                             + Vector3.Project(
                                                 currentShoulderLiftForceNewtons + currentLegLiftForceNewtons,
                                                 Vector3.up);
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

            // 按住上升键时允许全额爬升。空中 QB、AB 与主动刹车至少调用足以抵消
            // 当前微重力的推力，避免动作姿态把垂直支撑凭空丢掉。
            var liftCommand = frame.RiseHeld ? 1f : 0f;
            if (!grounded && (quickBoostApplying || AssaultBoostActive || braking))
            {
                var gravityCompensation = currentMassKg * gravityAcceleration / totalLiftNewtons;
                liftCommand = Mathf.Max(liftCommand, Mathf.Clamp01(gravityCompensation));
            }

            if (liftCommand <= Mathf.Epsilon)
            {
                return;
            }

            var horizontalIntent = ResolveHorizontalManeuverIntent(
                in frame, yawBasis, mainThrustDirection, quickBoostApplying);

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

            var inputDirection = yawBasis * new Vector3(
                frame.MoveLocal.x,
                0f,
                frame.MoveLocal.y);
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

        private Vector3 ApplyFlightEnvelope(Vector3 acceleration, bool bypass)
        {
            if (bypass || operationalSpeedEnvelope <= 0f ||
                acceleration.sqrMagnitude <= 0.0001f || velocity.sqrMagnitude <= 0.0001f)
            {
                return acceleration;
            }

            var speed = velocity.magnitude;
            var velocityDirection = velocity / speed;
            var outwardAcceleration = Vector3.Dot(acceleration, velocityDirection);
            if (outwardAcceleration <= 0f)
            {
                return acceleration;
            }

            var responseStart = Mathf.Max(0f, operationalSpeedEnvelope - envelopeResponseBand);
            if (speed <= responseStart)
            {
                return acceleration;
            }

            var outwardScale = speed >= operationalSpeedEnvelope
                ? 0f
                : Mathf.InverseLerp(operationalSpeedEnvelope, responseStart, speed);
            flightEnvelopeLimiting = outwardScale < 0.999f;
            return acceleration
                   - velocityDirection * outwardAcceleration * (1f - outwardScale);
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
