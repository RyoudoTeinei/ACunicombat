using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 以当前速度、持续操纵加速度、短时 QB 与环境重力绘制航轨预测线。
    /// 它只负责可视化，不参与运动积分，也不会把预测结果反馈给飞控。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LineRenderer))]
    public sealed class MechTrajectoryPredictor : MonoBehaviour
    {
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] [Range(16, 192)] private int sampleCount = 128;
        [SerializeField] private float predictionSeconds = 12f;
        [SerializeField] private float minimumDisplayDistance = 30f;
        [SerializeField] private float maximumDisplayDistance = 3000f;
        [SerializeField] private float minimumReferenceSpeed = 8f;
        [SerializeField] private float originHeight = 4f;
        [SerializeField] private float originForwardOffset = 3f;
        [SerializeField] private float lineWidth = 0.16f;
        [SerializeField] private Color trajectoryColor = new(0.12f, 1f, 0.32f, 0.95f);
        [Header("显示稳定")]
        [SerializeField] private float velocitySmoothingSeconds = 0.16f;
        [SerializeField] private float accelerationSmoothingSeconds = 0.28f;
        [SerializeField] private float referenceFrameSmoothingSeconds = 0.12f;

        private LineRenderer line;
        private Material runtimeMaterial;
        private readonly Vector3[] visiblePoints = new Vector3[192];
        private Vector3 smoothedVelocity;
        private Vector3 smoothedControlAcceleration;
        private Vector3 smoothedQuickBoostAcceleration;
        private Vector3 smoothedLogicalForward = Vector3.forward;
        private Vector3 smoothedReferenceUp = Vector3.up;
        private bool visualStateInitialized;

        public void Configure(MechFlightMotionController controller)
        {
            motion = controller;
            EnsureRenderer();
        }

        private void Awake()
        {
            if (motion == null)
            {
                motion = GetComponent<MechFlightMotionController>();
            }

            EnsureRenderer();
        }

        private void LateUpdate()
        {
            if (motion == null)
            {
                line.positionCount = 0;
                return;
            }

            var targetLogicalForward = motion.LogicalRotation * Vector3.forward;
            var targetVelocity = motion.Velocity;
            var targetControlAcceleration = motion.CurrentControlAcceleration;
            var targetQuickBoostAcceleration = motion.CurrentQuickBoostAcceleration;
            var targetReferenceUp = motion.ReferenceUp;
            if (targetVelocity.magnitude < minimumReferenceSpeed &&
                motion.CurrentControlAcceleration.sqrMagnitude < 0.0001f)
            {
                targetVelocity = targetLogicalForward * minimumReferenceSpeed;
            }

            if (!visualStateInitialized)
            {
                smoothedVelocity = targetVelocity;
                smoothedControlAcceleration = targetControlAcceleration;
                smoothedQuickBoostAcceleration = targetQuickBoostAcceleration;
                smoothedLogicalForward = targetLogicalForward.normalized;
                smoothedReferenceUp = targetReferenceUp.normalized;
                visualStateInitialized = true;
            }
            else
            {
                var deltaTime = Mathf.Max(0f, Time.deltaTime);
                smoothedVelocity = Vector3.Lerp(
                    smoothedVelocity,
                    targetVelocity,
                    ExponentialSmoothingFactor(deltaTime, velocitySmoothingSeconds));
                smoothedControlAcceleration = Vector3.Lerp(
                    smoothedControlAcceleration,
                    targetControlAcceleration,
                    ExponentialSmoothingFactor(deltaTime, accelerationSmoothingSeconds));
                smoothedQuickBoostAcceleration = Vector3.Lerp(
                    smoothedQuickBoostAcceleration,
                    targetQuickBoostAcceleration,
                    ExponentialSmoothingFactor(deltaTime, velocitySmoothingSeconds));
                var frameSmoothing = ExponentialSmoothingFactor(
                    deltaTime,
                    referenceFrameSmoothingSeconds);
                smoothedLogicalForward = SmoothDirection(
                    smoothedLogicalForward,
                    targetLogicalForward,
                    frameSmoothing);
                smoothedReferenceUp = SmoothDirection(
                    smoothedReferenceUp,
                    targetReferenceUp,
                    frameSmoothing);
            }

            var logicalForward = smoothedLogicalForward;
            var predictedVelocity = smoothedVelocity;
            var duration = Mathf.Max(0.1f, predictionSeconds);
            var stepSeconds = duration / (sampleCount - 1f);
            var controlAcceleration = smoothedControlAcceleration;
            var quickBoostAcceleration = smoothedQuickBoostAcceleration;
            var quickBoostRemaining = motion.CurrentQuickBoostRemaining;
            var gravity = motion.Grounded
                ? Vector3.zero
                : -smoothedReferenceUp * motion.GravityAcceleration;
            var displayOffset = smoothedReferenceUp * originHeight
                                + logicalForward * originForwardOffset;
            var predictedPosition = transform.position;
            var travelledDistance = 0f;
            var previousDisplayPosition = predictedPosition + displayOffset;

            var visiblePointCount = 0;
            var startDistance = Mathf.Max(0f, minimumDisplayDistance);
            var endDistance = Mathf.Max(startDistance, maximumDisplayDistance);
            for (var i = 1; i < sampleCount; i++)
            {
                predictedVelocity += (controlAcceleration + gravity) * stepSeconds;

                if (quickBoostRemaining > 0f)
                {
                    var quickBoostStep = Mathf.Min(stepSeconds, quickBoostRemaining);
                    predictedVelocity += quickBoostAcceleration * quickBoostStep;
                    quickBoostRemaining -= quickBoostStep;
                }

                if (!motion.Grounded && controlAcceleration.sqrMagnitude < 0.0001f &&
                    quickBoostRemaining <= 0f && motion.AirCoastingDragAcceleration > 0f)
                {
                    predictedVelocity = Vector3.MoveTowards(
                        predictedVelocity,
                        Vector3.zero,
                        motion.AirCoastingDragAcceleration * stepSeconds);
                }

                predictedPosition += predictedVelocity * stepSeconds;
                var displayPosition = predictedPosition + displayOffset;
                var segmentDistance = Vector3.Distance(previousDisplayPosition, displayPosition);
                var nextTravelledDistance = travelledDistance + segmentDistance;

                if (visiblePointCount == 0 && nextTravelledDistance >= startDistance)
                {
                    var startPoint = segmentDistance > Mathf.Epsilon
                        ? Vector3.Lerp(
                            previousDisplayPosition,
                            displayPosition,
                            Mathf.Clamp01((startDistance - travelledDistance) / segmentDistance))
                        : displayPosition;
                    visiblePoints[visiblePointCount++] = startPoint;
                }

                if (nextTravelledDistance >= endDistance)
                {
                    var remainingDistance = Mathf.Max(
                        0f, endDistance - travelledDistance);
                    var cappedPosition = segmentDistance > Mathf.Epsilon
                        ? Vector3.Lerp(
                            previousDisplayPosition,
                            displayPosition,
                            remainingDistance / segmentDistance)
                        : previousDisplayPosition;
                    if (visiblePointCount > 0 &&
                        Vector3.SqrMagnitude(
                            visiblePoints[visiblePointCount - 1] - cappedPosition) > 0.0001f)
                    {
                        visiblePoints[visiblePointCount++] = cappedPosition;
                    }

                    break;
                }

                if (visiblePointCount > 0 && visiblePointCount < visiblePoints.Length)
                {
                    visiblePoints[visiblePointCount++] = displayPosition;
                }

                travelledDistance = nextTravelledDistance;
                previousDisplayPosition = displayPosition;
            }

            line.positionCount = visiblePointCount;
            for (var i = 0; i < visiblePointCount; i++)
            {
                line.SetPosition(i, visiblePoints[i]);
            }
        }

        private void EnsureRenderer()
        {
            line ??= GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = false;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.numCapVertices = 4;
            line.numCornerVertices = 2;
            line.startWidth = lineWidth;
            line.endWidth = lineWidth * 0.35f;
            line.startColor = trajectoryColor;
            line.endColor = new Color(
                trajectoryColor.r,
                trajectoryColor.g,
                trajectoryColor.b,
                0f);
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = LightProbeUsage.Off;
            line.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(trajectoryColor, 0f),
                    new GradientColorKey(trajectoryColor, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(trajectoryColor.a, 0f),
                    new GradientAlphaKey(trajectoryColor.a * 0.75f, 0.65f),
                    new GradientAlphaKey(0f, 1f),
                });
            line.colorGradient = gradient;

            if (runtimeMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default")
                             ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                             ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                {
                    runtimeMaterial = new Material(shader)
                    {
                        name = "Runtime Trajectory HUD Material",
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    if (runtimeMaterial.HasProperty("_Color"))
                    {
                        runtimeMaterial.SetColor("_Color", Color.white);
                    }

                    if (runtimeMaterial.HasProperty("_BaseColor"))
                    {
                        runtimeMaterial.SetColor("_BaseColor", Color.white);
                    }

                    line.sharedMaterial = runtimeMaterial;
                }
            }
        }

        private void OnDestroy()
        {
            if (runtimeMaterial != null)
            {
                Destroy(runtimeMaterial);
            }
        }

        private void OnDisable()
        {
            visualStateInitialized = false;
        }

        private static float ExponentialSmoothingFactor(
            float deltaTime,
            float responseSeconds)
        {
            return responseSeconds <= 0.0001f
                ? 1f
                : 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / responseSeconds);
        }

        private static Vector3 SmoothDirection(
            Vector3 current,
            Vector3 target,
            float factor)
        {
            if (target.sqrMagnitude <= 0.0001f)
            {
                return current.sqrMagnitude > 0.0001f
                    ? current.normalized
                    : Vector3.forward;
            }

            if (current.sqrMagnitude <= 0.0001f)
            {
                return target.normalized;
            }

            return Vector3.Slerp(
                current.normalized,
                target.normalized,
                Mathf.Clamp01(factor)).normalized;
        }
    }

    /// <summary>
    /// Uses a public sensor track to command a translated trajectory parallel to the target.
    /// It never writes velocity directly; the motion controller allocates the requested
    /// acceleration across currently available physical thruster channels.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechOrbitSynchronizer : MonoBehaviour
    {
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private MechRadarController radar;
        [Header("闭环")]
        [SerializeField] private float velocityResponseSeconds = 1.1f;
        [SerializeField] private float positionCorrectionGain = 0.35f;
        [SerializeField] private float maximumPositionCorrectionSpeed = 45f;
        [SerializeField] private float maximumSynchronizationAcceleration = 32f;
        [SerializeField] private float throttleVelocityRange = 300f;
        [SerializeField] private float maximumLongitudinalVelocityBias = 300f;
        [SerializeField] private float targetVelocitySmoothingSeconds = 0.35f;
        [SerializeField] private float targetTurnReanchorDegrees = 2.5f;
        [SerializeField] private float targetSpeedReanchorThreshold = 8f;
        [Header("轨道显示")]
        [SerializeField] private float predictionSeconds = 12f;
        [SerializeField] private float minimumPathLength = 180f;
        [SerializeField] private float maximumPathLength = 3000f;
        [SerializeField] private float lineWidth = 0.22f;
        [SerializeField] private float dashLength = 42f;
        [SerializeField, Range(0.2f, 0.8f)] private float dashVisibleFraction = 0.55f;
        [SerializeField] private float dashScrollCyclesPerSecond = 1.15f;
        [SerializeField] private float dashAccelerationDeadzone = 0.3f;
        [SerializeField] private float pathStartDistance = 30f;
        [SerializeField] private float pathHalfWidth = 6f;
        [SerializeField] private float pathDirectionSmoothingSeconds = 0.32f;
        [SerializeField] private float pathLateralSmoothingSeconds = 0.18f;
        [SerializeField] private float pathLengthSmoothingSeconds = 0.24f;
        [SerializeField] private Color pathColor = new(0.1f, 0.95f, 0.85f, 0.9f);

        private TrackId synchronizedTrackId;
        private Vector3 desiredRelativeOffset;
        private Vector3 filteredTargetVelocity;
        private Vector3 trajectoryAnchorTargetVelocity;
        private Vector3 trajectoryAnchorDirection = Vector3.forward;
        private Vector3 parallelDirection = Vector3.forward;
        private float longitudinalVelocityBias;
        private bool lateralOverrideLastFrame;
        private bool longitudinalOverrideLastFrame;
        private bool active;
        private bool manualOverride;
        private float autoThrottleCommand;
        private LineRenderer leftPathLine;
        private LineRenderer rightPathLine;
        private Material pathMaterial;
        private Texture2D dashTexture;
        private Vector3 visualPathAnchor;
        private Vector3 visualPathDirection = Vector3.forward;
        private Vector3 visualPathRight = Vector3.right;
        private float visualPathSignedSpeed;
        private float dashTexturePhase;
        private bool pathVisualInitialized;

        public bool Active => active;
        public bool ManualOverride => manualOverride;
        public TrackId SynchronizedTrackId => synchronizedTrackId;
        public Vector3 ParallelDirection => parallelDirection;
        public Vector3 FilteredTargetVelocity => filteredTargetVelocity;
        public float LongitudinalVelocityBias => longitudinalVelocityBias;
        public float AutoThrottleCommand => autoThrottleCommand;
        public bool RequestsAutomaticDeparture =>
            active && motion != null && motion.Grounded;

        public void Configure(
            MechFlightMotionController flightMotion,
            MechRadarController radarController)
        {
            motion = flightMotion;
            radar = radarController;
            EnsurePathRenderer();
        }

        public bool Begin(in TrackSnapshot track)
        {
            if (motion == null)
            {
                motion = GetComponent<MechFlightMotionController>();
            }

            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
            }

            if (motion == null || radar == null || !track.TrackId.IsValid)
            {
                return false;
            }

            synchronizedTrackId = track.TrackId;
            var targetPosition = radar.PredictTrackPosition(track);
            filteredTargetVelocity = ToVector3(track.Velocity);
            parallelDirection = ResolveParallelDirection(
                filteredTargetVelocity,
                targetPosition - transform.position,
                motion.LogicalRotation * Vector3.forward);
            trajectoryAnchorTargetVelocity = filteredTargetVelocity;
            trajectoryAnchorDirection = parallelDirection;
            desiredRelativeOffset = transform.position - targetPosition;
            longitudinalVelocityBias = 0f;
            lateralOverrideLastFrame = false;
            longitudinalOverrideLastFrame = false;
            active = true;
            manualOverride = false;
            autoThrottleCommand = 0f;
            pathVisualInitialized = false;
            EnsurePathRenderer();
            SetPathVisible(true);
            return true;
        }

        public void Cancel()
        {
            active = false;
            synchronizedTrackId = default;
            manualOverride = false;
            autoThrottleCommand = 0f;
            pathVisualInitialized = false;
            motion?.ClearTrajectorySyncCommand();
            SetPathVisible(false);
        }

        public void Tick(in MechPlayerInputFrame frame, float deltaTime)
        {
            if (!active)
            {
                motion?.ClearTrajectorySyncCommand();
                return;
            }

            if (motion == null)
            {
                motion = GetComponent<MechFlightMotionController>();
            }

            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
            }

            if (motion == null || radar == null ||
                !radar.TryGetTrack(synchronizedTrackId, out var track) ||
                track.Lifecycle == TrackLifecycle.Degraded ||
                track.Lifecycle == TrackLifecycle.Dropped)
            {
                Cancel();
                return;
            }

            var targetPosition = radar.PredictTrackPosition(track);
            var measuredTargetVelocity = ToVector3(track.Velocity);
            var smoothing = 1f - Mathf.Exp(
                -Mathf.Max(0f, deltaTime) /
                Mathf.Max(0.02f, targetVelocitySmoothingSeconds));
            filteredTargetVelocity = Vector3.Lerp(
                filteredTargetVelocity,
                measuredTargetVelocity,
                smoothing);
            var newDirection = ResolveParallelDirection(
                filteredTargetVelocity,
                targetPosition - transform.position,
                motion.LogicalRotation * Vector3.forward);
            var targetChangedTrajectory =
                Vector3.Angle(trajectoryAnchorDirection, newDirection) >=
                targetTurnReanchorDegrees ||
                Mathf.Abs(
                    filteredTargetVelocity.magnitude -
                    trajectoryAnchorTargetVelocity.magnitude) >=
                targetSpeedReanchorThreshold;
            parallelDirection = newDirection;

            var quickBoostOverride = motion.QuickBoostActive || frame.QuickBoostEdge;
            var playerLateralOverride =
                Mathf.Abs(frame.MoveLocal.x) > 0.01f ||
                frame.RiseHeld ||
                frame.DescendHeld ||
                quickBoostOverride;
            var lateralOverride =
                playerLateralOverride || RequestsAutomaticDeparture;
            var directLongitudinalOverride =
                Mathf.Abs(frame.MoveLocal.y) > 0.01f ||
                frame.BrakeHeld ||
                motion.AssaultBoostActive ||
                quickBoostOverride;
            var throttleAdjusted =
                Mathf.Abs(frame.MainThrottleDelta) > 0.00001f;
            manualOverride =
                playerLateralOverride ||
                directLongitudinalOverride ||
                throttleAdjusted;

            var relativePosition = transform.position - targetPosition;
            var relativeVelocity = motion.Velocity - filteredTargetVelocity;
            if (targetChangedTrajectory)
            {
                desiredRelativeOffset = relativePosition;
                trajectoryAnchorTargetVelocity = filteredTargetVelocity;
                trajectoryAnchorDirection = parallelDirection;
            }

            if (lateralOverride ||
                (lateralOverrideLastFrame && !lateralOverride))
            {
                desiredRelativeOffset = relativePosition;
            }

            if (throttleAdjusted)
            {
                desiredRelativeOffset = relativePosition;
                longitudinalVelocityBias = Mathf.Clamp(
                    longitudinalVelocityBias +
                    frame.MainThrottleDelta * throttleVelocityRange,
                    -Mathf.Max(0f, maximumLongitudinalVelocityBias),
                    Mathf.Max(0f, maximumLongitudinalVelocityBias));
            }

            if (directLongitudinalOverride)
            {
                desiredRelativeOffset = relativePosition;
                longitudinalVelocityBias =
                    Vector3.Dot(relativeVelocity, parallelDirection);
            }
            else if (longitudinalOverrideLastFrame)
            {
                desiredRelativeOffset = relativePosition;
                longitudinalVelocityBias =
                    Vector3.Dot(relativeVelocity, parallelDirection);
            }

            var requestedAcceleration = CalculateSynchronizationAcceleration(
                transform.position,
                motion.Velocity,
                targetPosition,
                filteredTargetVelocity,
                desiredRelativeOffset,
                parallelDirection,
                longitudinalVelocityBias,
                lateralOverride,
                directLongitudinalOverride,
                velocityResponseSeconds,
                positionCorrectionGain,
                maximumPositionCorrectionSpeed,
                maximumSynchronizationAcceleration);
            motion.SetTrajectorySyncAcceleration(requestedAcceleration);
            autoThrottleCommand = Mathf.Clamp01(
                Mathf.Abs(Vector3.Dot(
                    requestedAcceleration,
                    parallelDirection)) /
                Mathf.Max(0.01f, maximumSynchronizationAcceleration));
            lateralOverrideLastFrame = lateralOverride;
            longitudinalOverrideLastFrame = directLongitudinalOverride;
            DrawParallelPath(deltaTime);
        }

        public static Vector3 CalculateSynchronizationAcceleration(
            Vector3 playerPosition,
            Vector3 playerVelocity,
            Vector3 targetPosition,
            Vector3 targetVelocity,
            Vector3 desiredOffset,
            Vector3 pathDirection,
            float longitudinalBias,
            bool lateralOverride,
            bool longitudinalOverride,
            float responseSeconds,
            float positionGain,
            float maximumCorrectionSpeed,
            float maximumAcceleration)
        {
            pathDirection = pathDirection.sqrMagnitude > 0.0001f
                ? pathDirection.normalized
                : Vector3.forward;
            var positionError =
                targetPosition + desiredOffset - playerPosition;
            var alongPositionError =
                Vector3.Project(positionError, pathDirection);
            var crossPositionError = positionError - alongPositionError;
            var positionCorrection = Vector3.ClampMagnitude(
                crossPositionError * Mathf.Max(0f, positionGain),
                Mathf.Max(0f, maximumCorrectionSpeed));
            if (Mathf.Abs(longitudinalBias) < 0.05f &&
                !longitudinalOverride)
            {
                positionCorrection += Vector3.ClampMagnitude(
                    alongPositionError * Mathf.Max(0f, positionGain),
                    Mathf.Max(0f, maximumCorrectionSpeed));
            }

            var desiredVelocity =
                targetVelocity +
                pathDirection * longitudinalBias +
                positionCorrection;
            var acceleration =
                (desiredVelocity - playerVelocity) /
                Mathf.Max(0.05f, responseSeconds);
            if (lateralOverride)
            {
                acceleration = Vector3.Project(acceleration, pathDirection);
            }

            if (longitudinalOverride)
            {
                acceleration -= Vector3.Project(acceleration, pathDirection);
            }

            return Vector3.ClampMagnitude(
                acceleration,
                Mathf.Max(0f, maximumAcceleration));
        }

        public static Vector3 ResolveParallelDirection(
            Vector3 targetVelocity,
            Vector3 directionToTarget,
            Vector3 fallbackForward)
        {
            if (targetVelocity.sqrMagnitude > 1f)
            {
                return targetVelocity.normalized;
            }

            if (directionToTarget.sqrMagnitude > 0.0001f)
            {
                return directionToTarget.normalized;
            }

            return fallbackForward.sqrMagnitude > 0.0001f
                ? fallbackForward.normalized
                : Vector3.forward;
        }

        private void Awake()
        {
            if (motion == null)
            {
                motion = GetComponent<MechFlightMotionController>();
            }

            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
            }

            EnsurePathRenderer();
            SetPathVisible(false);
        }

        private void DrawParallelPath(float deltaTime)
        {
            EnsurePathRenderer();
            if (leftPathLine == null || rightPathLine == null)
            {
                return;
            }

            var signedSpeed =
                Vector3.Dot(filteredTargetVelocity, parallelDirection) +
                longitudinalVelocityBias;
            var desiredDirection = signedSpeed >= 0f
                ? parallelDirection
                : -parallelDirection;
            var referenceUp = motion != null &&
                              motion.ReferenceUp.sqrMagnitude > 0.0001f
                ? motion.ReferenceUp.normalized
                : Vector3.up;
            var desiredAnchor = transform.position + referenceUp * 4f;
            var desiredRight = Vector3.Cross(referenceUp, desiredDirection);
            if (desiredRight.sqrMagnitude <= 0.0001f && motion != null)
            {
                desiredRight = motion.LogicalRotation * Vector3.right;
            }

            desiredRight = Vector3.ProjectOnPlane(
                desiredRight,
                desiredDirection).normalized;
            if (!pathVisualInitialized)
            {
                visualPathAnchor = desiredAnchor;
                visualPathDirection = desiredDirection.normalized;
                visualPathRight = desiredRight.sqrMagnitude > 0.0001f
                    ? desiredRight
                    : Vector3.right;
                visualPathSignedSpeed = signedSpeed;
                pathVisualInitialized = true;
            }
            else
            {
                var directionFactor = ExponentialSmoothingFactor(
                    deltaTime,
                    pathDirectionSmoothingSeconds);
                visualPathDirection = SmoothDirection(
                    visualPathDirection,
                    desiredDirection,
                    directionFactor);

                var anchorDelta = desiredAnchor - visualPathAnchor;
                var alongDelta = Vector3.Project(
                    anchorDelta,
                    visualPathDirection);
                var lateralDelta = anchorDelta - alongDelta;
                visualPathAnchor += alongDelta +
                                    lateralDelta * ExponentialSmoothingFactor(
                                        deltaTime,
                                        pathLateralSmoothingSeconds);

                desiredRight = Vector3.Cross(referenceUp, visualPathDirection);
                if (desiredRight.sqrMagnitude <= 0.0001f && motion != null)
                {
                    desiredRight = motion.LogicalRotation * Vector3.right;
                }

                desiredRight = Vector3.ProjectOnPlane(
                    desiredRight,
                    visualPathDirection).normalized;
                if (desiredRight.sqrMagnitude > 0.0001f)
                {
                    if (Vector3.Dot(desiredRight, visualPathRight) < 0f)
                    {
                        desiredRight = -desiredRight;
                    }

                    visualPathRight = SmoothDirection(
                        visualPathRight,
                        desiredRight,
                        directionFactor);
                }

                visualPathSignedSpeed = Mathf.Lerp(
                    visualPathSignedSpeed,
                    signedSpeed,
                    ExponentialSmoothingFactor(
                        deltaTime,
                        pathLengthSmoothingSeconds));
            }

            var fullPathLength = Mathf.Clamp(
                Mathf.Abs(visualPathSignedSpeed) *
                Mathf.Max(0.1f, predictionSeconds),
                Mathf.Max(10f, minimumPathLength),
                Mathf.Max(minimumPathLength, maximumPathLength));
            var hiddenDistance = Mathf.Clamp(
                pathStartDistance,
                0f,
                Mathf.Max(0f, fullPathLength - 1f));
            var lineStart = visualPathAnchor +
                            visualPathDirection * hiddenDistance;
            var lineEnd = visualPathAnchor +
                          visualPathDirection * fullPathLength;
            var laneOffset = visualPathRight *
                             Mathf.Max(0.1f, pathHalfWidth);
            SetLinePositions(
                leftPathLine,
                lineStart - laneOffset,
                lineEnd - laneOffset);
            SetLinePositions(
                rightPathLine,
                lineStart + laneOffset,
                lineEnd + laneOffset);
            if (pathMaterial != null)
            {
                pathMaterial.mainTextureScale = new Vector2(
                    (fullPathLength - hiddenDistance) /
                    Mathf.Max(1f, dashLength),
                    1f);
                UpdateDashAnimation(deltaTime);
            }
        }

        private void EnsurePathRenderer()
        {
            if (leftPathLine != null && rightPathLine != null)
            {
                return;
            }

            leftPathLine ??= EnsurePathLine(
                "OrbitSyncTrajectoryLeft",
                "OrbitSyncTrajectory");
            rightPathLine ??= EnsurePathLine(
                "OrbitSyncTrajectoryRight",
                null);

            if (dashTexture == null)
            {
                dashTexture = new Texture2D(64, 1, TextureFormat.RGBA32, false)
                {
                    name = "Runtime Orbit Sync Dash",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var visiblePixels = Mathf.Clamp(
                    Mathf.RoundToInt(
                        dashTexture.width *
                        Mathf.Clamp(dashVisibleFraction, 0.2f, 0.8f)),
                    1,
                    dashTexture.width - 1);
                for (var x = 0; x < dashTexture.width; x++)
                {
                    var color = Color.clear;
                    if (x < visiblePixels)
                    {
                        var normalized = visiblePixels > 1
                            ? x / (visiblePixels - 1f)
                            : 0.5f;
                        var pulse = 0.45f +
                                    Mathf.Sin(normalized * Mathf.PI) * 0.55f;
                        color = new Color(1f, 1f, 1f, pulse);
                    }

                    dashTexture.SetPixel(
                        x,
                        0,
                        color);
                }
                dashTexture.Apply(false, true);
            }

            if (pathMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default")
                             ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                             ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                {
                    pathMaterial = new Material(shader)
                    {
                        name = "Runtime Orbit Sync Path Material",
                        hideFlags = HideFlags.HideAndDontSave,
                        mainTexture = dashTexture,
                    };
                }
            }

            if (pathMaterial != null)
            {
                leftPathLine.sharedMaterial = pathMaterial;
                rightPathLine.sharedMaterial = pathMaterial;
            }
        }

        private void SetPathVisible(bool visible)
        {
            SetLineVisible(leftPathLine, visible);
            SetLineVisible(rightPathLine, visible);
        }

        private LineRenderer EnsurePathLine(
            string objectName,
            string legacyObjectName)
        {
            var child = transform.Find(objectName);
            if (child == null && !string.IsNullOrEmpty(legacyObjectName))
            {
                child = transform.Find(legacyObjectName);
                if (child != null)
                {
                    child.name = objectName;
                }
            }

            if (child == null)
            {
                var pathObject = new GameObject(objectName);
                child = pathObject.transform;
                child.SetParent(transform, false);
            }

            var renderer = child.GetComponent<LineRenderer>();
            if (renderer == null)
            {
                renderer = child.gameObject.AddComponent<LineRenderer>();
            }

            renderer.useWorldSpace = true;
            renderer.loop = false;
            renderer.alignment = LineAlignment.View;
            renderer.textureMode = LineTextureMode.Tile;
            renderer.numCapVertices = 2;
            renderer.numCornerVertices = 2;
            renderer.startWidth = lineWidth;
            renderer.endWidth = lineWidth * 0.65f;
            renderer.startColor = pathColor;
            renderer.endColor = new Color(
                pathColor.r,
                pathColor.g,
                pathColor.b,
                pathColor.a * 0.35f);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return renderer;
        }

        private static void SetLinePositions(
            LineRenderer renderer,
            Vector3 start,
            Vector3 end)
        {
            renderer.positionCount = 2;
            renderer.SetPosition(0, start);
            renderer.SetPosition(1, end);
        }

        private static void SetLineVisible(
            LineRenderer renderer,
            bool visible)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.enabled = visible;
            if (!visible)
            {
                renderer.positionCount = 0;
            }
        }

        private void UpdateDashAnimation(float deltaTime)
        {
            if (pathMaterial == null || motion == null)
            {
                return;
            }

            var alongAcceleration = Vector3.Dot(
                motion.CurrentControlAcceleration,
                parallelDirection);
            if (Mathf.Abs(alongAcceleration) >=
                Mathf.Max(0f, dashAccelerationDeadzone))
            {
                // Positive acceleration moves the highlight toward the path end.
                // Texture sampling runs in the opposite direction to visible UV motion.
                dashTexturePhase = Mathf.Repeat(
                    dashTexturePhase -
                    Mathf.Sign(alongAcceleration) *
                    Mathf.Max(0f, dashScrollCyclesPerSecond) *
                    Mathf.Max(0f, deltaTime),
                    1f);
            }

            pathMaterial.mainTextureOffset = new Vector2(
                dashTexturePhase,
                0f);
        }

        private void OnDisable()
        {
            motion?.ClearTrajectorySyncCommand();
        }

        private void OnDestroy()
        {
            if (pathMaterial != null)
            {
                Destroy(pathMaterial);
            }

            if (dashTexture != null)
            {
                Destroy(dashTexture);
            }
        }

        private static Vector3 ToVector3(Unity.Mathematics.double3 value)
        {
            return new Vector3(
                (float)value.x,
                (float)value.y,
                (float)value.z);
        }

        private static float ExponentialSmoothingFactor(
            float deltaTime,
            float responseSeconds)
        {
            return responseSeconds <= 0.0001f
                ? 1f
                : 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / responseSeconds);
        }

        private static Vector3 SmoothDirection(
            Vector3 current,
            Vector3 target,
            float factor)
        {
            if (target.sqrMagnitude <= 0.0001f)
            {
                return current.sqrMagnitude > 0.0001f
                    ? current.normalized
                    : Vector3.forward;
            }

            if (current.sqrMagnitude <= 0.0001f)
            {
                return target.normalized;
            }

            return Vector3.Slerp(
                current.normalized,
                target.normalized,
                Mathf.Clamp01(factor)).normalized;
        }
    }
}
