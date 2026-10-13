using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 第三人称镜头：初始在机体后方看向机体前方；水平鼠标 = Yaw（同时是机体转向来源，
    /// 由 MotionController 持有同一 Yaw 值）；垂直鼠标 = Pitch（仅镜头/瞄准，不给机体）；
    /// 按住 F 时鼠标只操作独立自由视角，不改变机体或火控姿态；
    /// Roll 由中键拖动旋转运动控制器的参考平面；镜头、机体、推进与瞄准共用该姿态基准。
    /// </summary>
    public sealed class MechThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] private Transform followTarget;
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private float distance = 27.741516f;
        [SerializeField] private float focusHeight = 10.403069f;
        [SerializeField, Min(0.01f)] private float followResponse = 8f;
        [Header("High-speed framing")]
        [SerializeField, Min(0.01f)] private float maxHorizontalLagRatio = 0.26f;
        [SerializeField, Min(0.01f)] private float maxVerticalLagRatio = 0.18f;
        [SerializeField, Min(0.01f)] private float maxDepthLagRatio = 0.45f;
        [SerializeField, Min(0.001f)] private float velocityLookbackSeconds = 0.075f;
        [SerializeField, Min(0f)] private float maxSpeedFovIncrease = 7f;
        [SerializeField, Min(1f)] private float fullSpeedFovAt = 350f;
        [Header("Combat composition")]
        [SerializeField, Min(0f)] private float baseCameraHeightOffset = 4.993473f;
        [SerializeField, Min(0f)] private float baseDistanceOffset = 4.161227f;
        [SerializeField, Min(0f)] private float sttCruiseShoulderOffset = 4.161227f;
        [SerializeField, Min(0f)] private float sttOcclusionPullback = 3.467689f;
        [SerializeField, Min(0.01f)] private float compositionResponse = 5f;
        [SerializeField, Min(0f)] private float shoulderReleaseSeconds = 1.5f;
        [SerializeField, Range(0f, 0.2f)] private float occlusionViewportPadding = 0.025f;
        [Header("Occlusion escape")]
        // Horizontal limit is the total shoulder offset; vertical escape is
        // measured relative to the base camera height.
        [SerializeField, Min(0f)] private float occlusionLateralLimit = 11.790144f;
        // 纵向上限刻意小于横向：抬降镜头对"机体压在画面下方"的基础构图破坏更明显。
        [SerializeField, Min(0f)] private float occlusionVerticalLimit = 6.935379f;
        [SerializeField, Range(0f, 0.1f)] private float occlusionEscapeMargin = 0.022f;
        [SerializeField, Min(0f)] private float occlusionEscapeSwitchMargin = 1.664491f;
        [SerializeField, Min(0f)] private float occlusionEscapeSwitchCooldown = 0.8f;
        [SerializeField, Min(0f)] private float occlusionHoldSeconds = 0.6f;
        [SerializeField, Min(0.02f)] private float rendererBoundsRefreshSeconds = 0.12f;
        [SerializeField] private Vector3 fallbackOwnshipBoundsSize = new Vector3(14f, 18f, 10f);
        [Header("Mouse aim cursor")]
        [SerializeField] private Vector2 aimViewportCenter = new Vector2(0.5f, 0.56f);
        [SerializeField] private Vector2 aimCursorHalfExtent = new Vector2(0.30f, 0.25f);
        [SerializeField, Min(0.01f)] private float aimCursorSensitivity = 1f;
        [SerializeField, Range(0f, 0.5f)] private float aimDeadZone = 0.06f;
        [SerializeField, Min(1f)] private float pitchTurnRateDegrees = 60f;
        [Header("Free look")]
        [SerializeField, Min(0.01f)] private float freeLookDegreesPerPixel = 0.14f;
        [SerializeField, Range(10f, 89f)] private float freeLookPitchLimitDegrees = 80f;
        [SerializeField, Min(0.01f)] private float freeLookReturnSmoothTime = 0.18f;
        [Header("Space-scale rendering")]
        [SerializeField, Min(0.01f)] private float nearClipDistance = 0.5f;
        [SerializeField, Min(1000f)] private float farClipDistance = 100000f;
        [Header("View zoom")]
        [SerializeField] private Key zoomKey = Key.CapsLock;
        [SerializeField, Range(0.05f, 0.8f)] private float overviewZoomOwnshipAlpha = 0.24f;
        [SerializeField, Range(0.05f, 1f)] private float overviewZoomAimSensitivity = 0.55f;
        [SerializeField, Range(0.02f, 1f)] private float precisionZoomAimSensitivity = 0.24f;
        [SerializeField, Range(0.01f, 1f)] private float maximumZoomAimSensitivity = 0.13f;
        [SerializeField] private bool zoomTracksSttTarget = true;
        [SerializeField, Min(1f)] private float zoomTrackingPitchRateDegrees = 75f;
        [SerializeField, Min(1f)] private float zoomTrackingYawCaptureDegrees = 18f;
        [Header("Tracked target stabilization")]
        [SerializeField, Min(0.02f)] private float trackedPositionSmoothingSeconds = 0.2f;
        [SerializeField, Min(0.02f)] private float trackedVelocitySmoothingSeconds = 0.3f;
        [SerializeField, Min(1f)] private float maximumTrackCorrectionSpeed = 220f;

        private float pitchDegrees = 8f;
        private Vector3 localLag;
        private Vector3 localLagVelocity;
        private Camera controlledCamera;
        private float baseFieldOfView;
        private float fieldOfViewVelocity;
        private bool fieldOfViewInitialized;
        private int zoomStage;
        private Renderer[] zoomRenderers = System.Array.Empty<Renderer>();
        private bool[] zoomRendererPreviousStates = System.Array.Empty<bool>();
        private Material[][] zoomRendererPreviousMaterials =
            System.Array.Empty<Material[]>();
        private Material zoomGhostMaterial;
        private OwnshipZoomVisualState ownshipZoomVisualState;
        private Vector2 aimViewportPoint;
        private bool aimCursorInitialized;
        private MechRadarController radar;
        private Renderer[] followRenderers = new Renderer[0];
        private Bounds ownshipLocalBounds;
        private Bounds ownshipWorldBounds;
        private bool hasOwnshipLocalBounds;
        private bool hasOwnshipWorldBounds;
        private float nextBoundsRefreshTime;
        private readonly Vector3[] ownshipWorldCorners = new Vector3[8];
        private readonly Vector3[] ownshipCameraCorners = new Vector3[8];
        private Vector3 compositionTargetCameraPosition;
        private Vector2 compositionProjectionScale;
        private Vector3 compositionLocalOffset;
        private Vector3 compositionLocalOffsetVelocity;
        private TrackId compositionTrackId;
        private float shoulderSign = 1f;
        private float shoulderBiasUntil;
        private MechTrackControlFilter zoomTrackControlFilter;
        private MechTrackControlFilter compositionTrackControlFilter;
        private uint observedReferenceFrameVersion;
        // -1 = 无避让，0 = 横向，1 = 纵向。
        private int occlusionEscapeAxis = -1;
        private float occlusionEscapeSign = 1f;
        private float occlusionEscapeMeters;
        private float occlusionEscapeCoordinate;
        private bool occlusionEscapeClamped;
        private float occlusionEscapeSwitchAllowedAt;
        private float occlusionSafeSince = float.NaN;
        private int pendingEscapeAxis = -1;
        private float pendingEscapeSign;
        private float pendingEscapeSince;
        private bool freeLookActive;
        private float freeLookYawDegrees;
        private float freeLookPitchDegrees;
        private float freeLookYawVelocity;
        private float freeLookPitchVelocity;

        public float PitchDegrees => motion != null && motion.LocalAttitudeControl
            ? motion.LogicalPitchDegrees : pitchDegrees;
        public Vector2 AimViewportPoint
        {
            get
            {
                EnsureAimCursorInitialized();
                return aimViewportPoint;
            }
        }

        public Quaternion AimRotation => ComputeAimRotation();
        public bool FreeLookActive => freeLookActive;
        public bool FreeLookViewOffsetActive => HasFreeLookOffset;
        public bool IsZoomActive
        {
            get
            {
                var keyboard = Keyboard.current;
                return keyboard != null &&
                       keyboard[zoomKey]?.isPressed == true;
            }
        }

        public int ZoomMagnification => Mathf.RoundToInt(ResolveZoomMagnification(zoomStage));

        public bool ZoomTracksSttTarget => zoomTracksSttTarget;

        public void SetZoomTracksSttTarget(bool enabled)
        {
            zoomTracksSttTarget = enabled;
        }

        /// <summary>
        /// Returns the combat aim ray without the free-look orbit offset. This keeps
        /// weapons and fire-control designation attached to the mech while the view moves.
        /// </summary>
        public Ray GetCombatAimRay()
        {
            EnsureAimCursorInitialized();
            if (motion == null || followTarget == null)
            {
                return new Ray(transform.position, transform.forward);
            }

            InitializeFieldOfView();
            var aimRotation = ComputeAimRotation();
            var fieldOfView = controlledCamera != null
                ? controlledCamera.fieldOfView
                : 60f;
            var aspect = controlledCamera != null
                ? Mathf.Max(0.1f, controlledCamera.aspect)
                : Mathf.Max(0.1f, (float)Screen.width / Mathf.Max(1f, Screen.height));
            var halfHeight = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            var viewportPoint = AimViewportPoint;
            var localDirection = new Vector3(
                (viewportPoint.x - 0.5f) * 2f * halfHeight * aspect,
                (viewportPoint.y - 0.5f) * 2f * halfHeight,
                1f).normalized;
            var origin = followTarget.position + (motion.LogicalRotation * Vector3.up) * focusHeight;
            return new Ray(origin, aimRotation * localDirection);
        }

        public void Configure(Transform target, MechFlightMotionController motionController)
        {
            if (followTarget != target)
            {
                radar = null;
                followRenderers = System.Array.Empty<Renderer>();
                hasOwnshipLocalBounds = false;
                nextBoundsRefreshTime = 0f;
            }
            followTarget = target;
            motion = motionController;
            if (motion != null)
            {
                pitchDegrees = motion.ConstrainPitchForCurrentMode(pitchDegrees);
            }
            observedReferenceFrameVersion =
                motion != null ? motion.ReferenceFrameVersion : 0u;
            ResolveCombatReferences();
            ApplyClipPlanes();
            SnapBehindTarget();
        }

        public void ConfigureViewDistance(float nearDistance, float farDistance)
        {
            nearClipDistance = Mathf.Max(0.01f, nearDistance);
            farClipDistance = Mathf.Max(nearClipDistance + 1f, farDistance);
            ApplyClipPlanes();
        }

        public void ConfigureFraming(float followDistance, float targetFocusHeight, float initialPitchDegrees)
        {
            distance = Mathf.Max(1f, followDistance);
            focusHeight = Mathf.Max(0f, targetFocusHeight);
            pitchDegrees = motion != null
                ? motion.ConstrainPitchForCurrentMode(initialPitchDegrees)
                : Mathf.DeltaAngle(0f, initialPitchDegrees);
            SnapBehindTarget();
        }

        public void ConfigureHighSpeedFraming(
            float response,
            float horizontalLagRatio,
            float verticalLagRatio,
            float depthLagRatio)
        {
            followResponse = Mathf.Max(0.01f, response);
            maxHorizontalLagRatio = Mathf.Max(0.01f, horizontalLagRatio);
            maxVerticalLagRatio = Mathf.Max(0.01f, verticalLagRatio);
            maxDepthLagRatio = Mathf.Max(0.01f, depthLagRatio);
        }

        /// <summary>启动即位于机体后方、朝向机体前方（验收 6.1）。</summary>
        public void SnapBehindTarget()
        {
            aimViewportPoint = aimViewportCenter;
            aimCursorInitialized = true;
            if (followTarget == null || motion == null)
            {
                return;
            }

            localLag = Vector3.zero;
            localLagVelocity = Vector3.zero;
            compositionLocalOffset = BaseCompositionOffset();
            compositionLocalOffsetVelocity = Vector3.zero;
            zoomTrackControlFilter.Reset();
            ClearCompositionTarget();
            shoulderBiasUntil = 0f;
            fieldOfViewVelocity = 0f;
            ResetFreeLookImmediate();
            motion.EnableLocalAttitudeControl(pitchDegrees);
            motion.StopLocalAttitudeInput();
            var rotation = ComputeViewRotation();
            transform.SetPositionAndRotation(
                ComputePosition(rotation) + rotation * compositionLocalOffset,
                rotation);
            InitializeFieldOfView();
            if (controlledCamera != null)
            {
                controlledCamera.fieldOfView = baseFieldOfView;
            }
        }

        public void ResetToReferenceHorizon()
        {
            pitchDegrees = 0f;
            aimViewportPoint = aimViewportCenter;
            aimCursorInitialized = true;
            ResetFreeLookImmediate();
            ClearCompositionTarget();
            if (motion == null)
            {
                return;
            }

            if (!motion.LocalAttitudeControl) motion.SetAimPitch(0f);
            motion.StopLocalAttitudeInput();
            observedReferenceFrameVersion = motion.ReferenceFrameVersion;
        }

        public void UpdateLook(in MechPlayerInputFrame frame)
        {
            if (followTarget == null || motion == null)
            {
                return;
            }

            if (MechSolarSystemMap.IsAnyMapVisible ||
                MechAssemblyMenu.IsAnyMenuVisible ||
                MechSystemsRadialMenu.IsAnyMenuVisible)
            {
                EndFreeLook();
                motion.StopLocalAttitudeInput();
                return;
            }

            motion.EnableLocalAttitudeControl(pitchDegrees);
            EnsureAimCursorInitialized();
            if (frame.FreeLookHeld)
            {
                UpdateFreeLook(in frame);
                return;
            }

            EndFreeLook();
            if (TryTrackSttWhileZoomed())
            {
                return;
            }

            var screenSize = new Vector2(
                Mathf.Max(1f, Screen.width),
                Mathf.Max(1f, Screen.height));
            var viewportDelta = new Vector2(
                frame.MouseDelta.x / screenSize.x,
                frame.MouseDelta.y / screenSize.y);
            aimViewportPoint +=
                viewportDelta *
                aimCursorSensitivity *
                GetZoomAimSensitivityMultiplier();

            var extents = new Vector2(
                Mathf.Max(0.01f, aimCursorHalfExtent.x),
                Mathf.Max(0.01f, aimCursorHalfExtent.y));
            var normalizedOffset = aimViewportPoint - aimViewportCenter;
            normalizedOffset = new Vector2(
                normalizedOffset.x / extents.x,
                normalizedOffset.y / extents.y);
            if (normalizedOffset.sqrMagnitude > 1f)
            {
                normalizedOffset.Normalize();
            }

            aimViewportPoint =
                aimViewportCenter + Vector2.Scale(normalizedOffset, extents);
            motion.SetLocalAttitudeInput(
                ApplyDeadZone(normalizedOffset.x, aimDeadZone),
                -ApplyDeadZone(normalizedOffset.y, aimDeadZone) * pitchTurnRateDegrees);
        }

        private void UpdateFreeLook(in MechPlayerInputFrame frame)
        {
            if (!freeLookActive)
            {
                freeLookActive = true;
                aimViewportPoint = aimViewportCenter;
                zoomTrackControlFilter.Reset();
            }

            freeLookYawDegrees = Mathf.DeltaAngle(
                0f,
                freeLookYawDegrees + frame.MouseDelta.x * freeLookDegreesPerPixel);
            freeLookPitchDegrees = Mathf.Clamp(
                freeLookPitchDegrees - frame.MouseDelta.y * freeLookDegreesPerPixel,
                -freeLookPitchLimitDegrees,
                freeLookPitchLimitDegrees);
            freeLookYawVelocity = 0f;
            freeLookPitchVelocity = 0f;
            motion.StopLocalAttitudeInput();
        }

        private void EndFreeLook()
        {
            if (!freeLookActive)
            {
                return;
            }

            freeLookActive = false;
            motion?.StopLocalAttitudeInput();
        }

        private void EnsureAimCursorInitialized()
        {
            if (aimCursorInitialized)
            {
                return;
            }

            aimViewportPoint = aimViewportCenter;
            aimCursorInitialized = true;
        }

        private static float ApplyDeadZone(float value, float deadZone)
        {
            var magnitude = Mathf.Abs(value);
            if (magnitude <= deadZone)
            {
                return 0f;
            }

            return Mathf.Sign(value) *
                   Mathf.InverseLerp(deadZone, 1f, magnitude);
        }

        private float GetZoomAimSensitivityMultiplier()
        {
            if (!IsZoomActive)
            {
                return 1f;
            }

            return zoomStage switch
            {
                2 => maximumZoomAimSensitivity,
                1 => precisionZoomAimSensitivity,
                _ => overviewZoomAimSensitivity,
            };
        }

        private bool TryTrackSttWhileZoomed()
        {
            if (!zoomTracksSttTarget || !IsZoomActive)
            {
                zoomTrackControlFilter.Reset();
                return false;
            }

            ResolveCombatReferences();
            if (radar == null || !radar.SttTrackId.IsValid ||
                !radar.TryGetTrack(radar.SttTrackId, out var sttTrack))
            {
                zoomTrackControlFilter.Reset();
                return false;
            }

            var focusPoint = followTarget.position + (motion.LogicalRotation * Vector3.up) * focusHeight;
            var direction = StabilizeTrackPosition(
                                ref zoomTrackControlFilter,
                                in sttTrack,
                                Time.unscaledDeltaTime) -
                            focusPoint;
            if (direction.sqrMagnitude <= 0.001f)
            {
                return false;
            }

            SteerLocallyToward(direction);
            aimViewportPoint = Vector2.MoveTowards(
                aimViewportPoint,
                aimViewportCenter,
                Time.unscaledDeltaTime * 2.5f);
            return true;
        }

        private void SteerLocallyToward(Vector3 direction)
        {
            var localDirection = Quaternion.Inverse(motion.LogicalRotation) * direction.normalized;
            var desiredYaw = Mathf.Atan2(localDirection.x, localDirection.z) *
                Mathf.Rad2Deg;
            var horizontal = Mathf.Sqrt(
                localDirection.x * localDirection.x +
                localDirection.z * localDirection.z);
            var desiredPitch = -Mathf.Atan2(localDirection.y, horizontal) *
                Mathf.Rad2Deg;
            motion.SetLocalAttitudeInput(
                Mathf.Clamp(desiredYaw / Mathf.Max(1f, zoomTrackingYawCaptureDegrees), -1f, 1f),
                Mathf.Clamp(desiredPitch / 0.2f,
                    -zoomTrackingPitchRateDegrees, zoomTrackingPitchRateDegrees));
        }

        public void Follow(float deltaTime)
        {
            if (MechAssemblyMenu.IsAnyMenuVisible ||
                followTarget == null || motion == null)
            {
                return;
            }

            if (observedReferenceFrameVersion != motion.ReferenceFrameVersion)
            {
                ResetToReferenceHorizon();
            }

            UpdateFreeLookReturn(deltaTime);
            var rotation = ComputeViewRotation();
            var desiredPosition = ComputePosition(rotation);
            var targetLag = ComputeTargetLocalLag(rotation);
            SmoothLocalLag(targetLag, deltaTime);
            // Projection and the rendered camera must use the same frame's FOV,
            // rotation and follow position, never last frame's camera transform.
            UpdateFieldOfView(deltaTime);
            var targetComposition = BaseCompositionOffset();
            if (HasFreeLookOffset)
            {
                ClearCompositionTarget();
            }
            else
            {
                targetComposition = ComputeTargetCompositionOffset(
                    desiredPosition + rotation * localLag, rotation);
            }
            SmoothCompositionOffset(targetComposition, deltaTime);
            transform.SetPositionAndRotation(
                desiredPosition + rotation * (localLag + compositionLocalOffset),
                rotation);
        }

        public void Tick(in MechPlayerInputFrame frame, float deltaTime)
        {
            UpdateLook(in frame);
            Follow(deltaTime);
        }

        private Quaternion ComputeAimRotation() =>
            motion.ResolveReferenceViewRotation(pitchDegrees);

        private Quaternion ComputeViewRotation()
        {
            return ComputeAimRotation() *
                   Quaternion.AngleAxis(freeLookYawDegrees, Vector3.up) *
                   Quaternion.AngleAxis(freeLookPitchDegrees, Vector3.right);
        }

        private bool HasFreeLookOffset =>
            freeLookActive ||
            Mathf.Abs(freeLookYawDegrees) > 0.01f ||
            Mathf.Abs(freeLookPitchDegrees) > 0.01f;

        private void UpdateFreeLookReturn(float deltaTime)
        {
            if (freeLookActive)
            {
                return;
            }

            var smoothTime = Mathf.Max(0.01f, freeLookReturnSmoothTime);
            freeLookYawDegrees = Mathf.SmoothDampAngle(
                freeLookYawDegrees,
                0f,
                ref freeLookYawVelocity,
                smoothTime,
                Mathf.Infinity,
                Mathf.Max(0f, deltaTime));
            freeLookPitchDegrees = Mathf.SmoothDampAngle(
                freeLookPitchDegrees,
                0f,
                ref freeLookPitchVelocity,
                smoothTime,
                Mathf.Infinity,
                Mathf.Max(0f, deltaTime));

            if (Mathf.Abs(freeLookYawDegrees) < 0.01f &&
                Mathf.Abs(freeLookPitchDegrees) < 0.01f)
            {
                ResetFreeLookImmediate();
            }
        }

        private void ResetFreeLookImmediate()
        {
            freeLookActive = false;
            freeLookYawDegrees = 0f;
            freeLookPitchDegrees = 0f;
            freeLookYawVelocity = 0f;
            freeLookPitchVelocity = 0f;
        }

        private Vector3 ComputePosition(Quaternion rotation)
        {
            var focusPoint = followTarget.position + (motion.LogicalRotation * Vector3.up) * focusHeight;
            return focusPoint - rotation * Vector3.forward * distance;
        }

        private Vector3 ComputeTargetLocalLag(Quaternion cameraRotation)
        {
            // Velocity is converted into camera space so sideways motion changes composition,
            // while forward acceleration mainly changes camera distance. Soft limiting avoids
            // the mechanical stop produced by a hard clamp at the framing boundary.
            var localVelocity = Quaternion.Inverse(cameraRotation) * motion.Velocity;
            var requestedLag = -localVelocity * velocityLookbackSeconds;
            return new Vector3(
                SoftLimit(requestedLag.x, distance * maxHorizontalLagRatio),
                SoftLimit(requestedLag.y, distance * maxVerticalLagRatio),
                SoftLimit(requestedLag.z, distance * maxDepthLagRatio));
        }

        private void ResetOcclusionEscape()
        {
            occlusionEscapeAxis = -1;
            occlusionEscapeMeters = 0f;
            occlusionEscapeCoordinate = 0f;
            occlusionEscapeClamped = false;
            occlusionSafeSince = float.NaN;
            occlusionEscapeSwitchAllowedAt = 0f;
            pendingEscapeAxis = -1;
        }

        private void SetCompositionTarget(TrackId trackId)
        {
            if (compositionTrackId.Equals(trackId))
            {
                return;
            }
            compositionTrackId = trackId;
            compositionTrackControlFilter.Reset();
            ResetOcclusionEscape();
        }

        private void ClearCompositionTarget()
        {
            compositionTrackId = default;
            compositionTrackControlFilter.Reset();
            ResetOcclusionEscape();
        }

        private Vector3 ComputeTargetCompositionOffset(
            Vector3 followPosition, Quaternion rotation)
        {
            ResolveCombatReferences();
            var targetOffset = BaseCompositionOffset();
            var now = Time.unscaledTime;

            // 巡航肩位仍然只在 STT 下生效。它是交战构图，不该被玩家没有主动关注的航迹带偏；
            // 遮挡避让的目标范围比它更宽，见 TryResolveOcclusionTarget。
            var hasStt = false;
            var sttTrack = default(TrackSnapshot);
            if (radar != null && radar.SttTrackId.IsValid)
            {
                hasStt = radar.TryGetTrack(radar.SttTrackId, out sttTrack);
            }

            if (hasStt)
            {
                shoulderBiasUntil = now + Mathf.Max(0.5f, shoulderReleaseSeconds);
                targetOffset.x = shoulderSign * sttCruiseShoulderOffset;
            }
            else
            {
                if (now < shoulderBiasUntil)
                {
                    targetOffset.x = shoulderSign * sttCruiseShoulderOffset;
                }
            }

            if (!TryResolveOcclusionTarget(hasStt, sttTrack, out var avoidTrack))
            {
                ClearCompositionTarget();
                return targetOffset;
            }

            SetCompositionTarget(avoidTrack.TrackId);
            ApplyOcclusionEscape(
                StabilizeTrackPosition(
                    ref compositionTrackControlFilter,
                    in avoidTrack,
                    Time.unscaledDeltaTime),
                now,
                followPosition,
                rotation,
                ref targetOffset);
            return targetOffset;
        }

        /// <summary>
        /// 遮挡避让的目标：STT → 准星候选 → 最优航迹兜底。
        /// 基础肩位只由 STT 决定，避让也可保护未锁定的准星候选或最佳航迹。
        /// </summary>
        private bool TryResolveOcclusionTarget(
            bool hasStt,
            in TrackSnapshot sttTrack,
            out TrackSnapshot track)
        {
            if (hasStt)
            {
                track = sttTrack;
                return true;
            }

            track = default;
            if (radar == null)
            {
                return false;
            }

            if (radar.CandidateTrackId.IsValid &&
                radar.TryGetTrack(radar.CandidateTrackId, out track))
            {
                return true;
            }

            return radar.TryGetBestTrack(out track);
        }

        private void ApplyOcclusionEscape(
            Vector3 targetPosition,
            float now,
            Vector3 followPosition,
            Quaternion rotation,
            ref Vector3 targetOffset)
        {
            // Evaluate the intended base/cruise pose, including current velocity
            // lag but excluding escape. A successful escape must not erase its
            // own cause and send the camera back into the same obstruction.
            if (!TryGetOcclusionProjection(
                targetPosition, followPosition + rotation * targetOffset, rotation,
                out var targetViewport,
                out var ownshipRect))
            {
                ResetOcclusionEscape();
                return;
            }

            var point = new Vector2(targetViewport.x, targetViewport.y);
            var entryRect = ExpandRect(ownshipRect, occlusionViewportPadding);
            var exitRect = ExpandRect(entryRect, occlusionEscapeMargin);
            var occluded = entryRect.Contains(point);
            if (occluded)
            {
                occlusionSafeSince = float.NaN;
                UpdateEscapeDecision(targetOffset, now);
            }
            else if (occlusionEscapeAxis >= 0 && exitRect.Contains(point))
            {
                // Spatial hysteresis: a target grazing the entry boundary has
                // not yet made the unescaped composition safe to return to.
                occlusionSafeSince = float.NaN;
                pendingEscapeAxis = -1;
            }
            else if (occlusionEscapeAxis >= 0)
            {
                pendingEscapeAxis = -1;
                if (float.IsNaN(occlusionSafeSince)) occlusionSafeSince = now;
                if (now - occlusionSafeSince >= Mathf.Max(0.1f, occlusionHoldSeconds))
                    ResetOcclusionEscape();
            }

            if (occlusionEscapeAxis == 0)
            {
                // Hold the solved absolute coordinate if the same target enters
                // or leaves STT during release hysteresis; do not add the new
                // cruise shoulder a second time to an old relative displacement.
                var limit = Mathf.Max(Mathf.Abs(targetOffset.x), occlusionLateralLimit);
                targetOffset.x = Mathf.Clamp(occlusionEscapeCoordinate, -limit, limit);
            }
            else if (occlusionEscapeAxis == 1)
            {
                targetOffset.y = Mathf.Clamp(occlusionEscapeCoordinate,
                    targetOffset.y - occlusionVerticalLimit, targetOffset.y + occlusionVerticalLimit);
            }

            if (occlusionEscapeAxis >= 0 && occlusionEscapeClamped)
            {
                targetOffset.z -= sttOcclusionPullback;
            }
        }

        /// <summary>
        /// Solve displacement from the stable reference pose. Perspective depth
        /// of both the ownship corners and the target contributes to parallax.
        /// </summary>
        private void UpdateEscapeDecision(
            Vector3 baseOffset,
            float now)
        {
            var bestAxis = -1;
            var bestSign = 1f;
            var bestCost = float.PositiveInfinity;
            var bestFits = false;
            for (var axis = 0; axis < 2; axis++)
            {
                for (var direction = 0; direction < 2; direction++)
                {
                    var sign = direction == 0 ? 1f : -1f;
                    var cost = ComputeEscapeDistance(axis, sign);
                    var limit = EscapeLimit(axis, sign, baseOffset);
                    if (limit <= 0f || !float.IsFinite(cost)) continue;
                    var fits = cost <= limit;
                    if (bestAxis < 0 || (fits && !bestFits) ||
                        (fits == bestFits && cost < bestCost))
                    {
                        bestAxis = axis;
                        bestSign = sign;
                        bestCost = cost;
                        bestFits = fits;
                    }
                }
            }
            if (bestAxis < 0)
            {
                ResetOcclusionEscape();
                return;
            }

            var sameChoice = occlusionEscapeAxis == bestAxis &&
                             occlusionEscapeSign == bestSign;
            if (occlusionEscapeAxis >= 0 && !sameChoice)
            {
                var currentCost = ComputeEscapeDistance(occlusionEscapeAxis, occlusionEscapeSign);
                var currentLimit = EscapeLimit(occlusionEscapeAxis, occlusionEscapeSign, baseOffset);
                var currentFits = currentCost <= currentLimit;
                var materiallyBetter = (bestFits && !currentFits) ||
                    (bestFits == currentFits &&
                     bestCost + occlusionEscapeSwitchMargin < currentCost);
                if (materiallyBetter)
                {
                    if (pendingEscapeAxis != bestAxis || pendingEscapeSign != bestSign)
                    {
                        pendingEscapeAxis = bestAxis;
                        pendingEscapeSign = bestSign;
                        pendingEscapeSince = now;
                    }
                }
                else
                {
                    pendingEscapeAxis = -1;
                }
                if (!materiallyBetter || now < occlusionEscapeSwitchAllowedAt ||
                    now - pendingEscapeSince < occlusionEscapeSwitchCooldown)
                {
                    bestAxis = occlusionEscapeAxis;
                    bestSign = occlusionEscapeSign;
                    bestCost = currentCost;
                }
            }
            else
            {
                pendingEscapeAxis = -1;
            }

            if (occlusionEscapeAxis != bestAxis || occlusionEscapeSign != bestSign)
            {
                occlusionEscapeSwitchAllowedAt = now + occlusionEscapeSwitchCooldown;
                pendingEscapeAxis = -1;
            }
            var finalLimit = EscapeLimit(bestAxis, bestSign, baseOffset);
            occlusionEscapeClamped = bestCost > finalLimit;
            occlusionEscapeAxis = bestAxis;
            occlusionEscapeSign = bestSign;
            occlusionEscapeMeters = Mathf.Clamp(bestCost, 0f, finalLimit);
            occlusionEscapeCoordinate = baseOffset[bestAxis] + bestSign * occlusionEscapeMeters;
        }

        private float EscapeLimit(int axis, float sign, Vector3 baseOffset)
        {
            return axis == 0
                ? Mathf.Max(0f, Mathf.Max(Mathf.Abs(baseOffset.x), occlusionLateralLimit) - sign * baseOffset.x)
                : Mathf.Max(0f, occlusionVerticalLimit);
        }

        private float ComputeEscapeDistance(int axis, float sign)
        {
            var target = compositionTargetCameraPosition;
            var targetSlope = target[axis] / target.z;
            var marginSlope = (occlusionViewportPadding + occlusionEscapeMargin) *
                              compositionProjectionScale[axis];
            var required = 0f;
            for (var i = 0; i < ownshipCameraCorners.Length; i++)
            {
                var corner = ownshipCameraCorners[i];
                var parallax = 1f / corner.z - 1f / target.z;
                if (parallax <= 0.000001f) return float.PositiveInfinity;
                var offset = (sign * (corner[axis] / corner.z - targetSlope) + marginSlope) / parallax;
                required = Mathf.Max(required, offset);
            }
            return required;
        }

        private Vector3 StabilizeTrackPosition(
            ref MechTrackControlFilter filter,
            in TrackSnapshot track,
            float deltaTime)
        {
            var measuredVelocity = new Vector3(
                (float)track.Velocity.x,
                (float)track.Velocity.y,
                (float)track.Velocity.z);
            return filter.Update(
                track.TrackId,
                radar.PredictTrackPosition(in track),
                measuredVelocity,
                deltaTime,
                trackedPositionSmoothingSeconds,
                trackedVelocitySmoothingSeconds,
                maximumTrackCorrectionSpeed);
        }

        private Vector3 BaseCompositionOffset() =>
            new Vector3(0f, baseCameraHeightOffset, -baseDistanceOffset);

        private void SmoothCompositionOffset(Vector3 targetOffset, float deltaTime)
        {
            var smoothTime = 1f / Mathf.Max(0.01f, compositionResponse);
            compositionLocalOffset.x = Mathf.SmoothDamp(
                compositionLocalOffset.x,
                targetOffset.x,
                ref compositionLocalOffsetVelocity.x,
                smoothTime,
                Mathf.Infinity,
                deltaTime);
            compositionLocalOffset.y = Mathf.SmoothDamp(
                compositionLocalOffset.y,
                targetOffset.y,
                ref compositionLocalOffsetVelocity.y,
                smoothTime * 1.2f,
                Mathf.Infinity,
                deltaTime);
            compositionLocalOffset.z = Mathf.SmoothDamp(
                compositionLocalOffset.z,
                targetOffset.z,
                ref compositionLocalOffsetVelocity.z,
                smoothTime * 1.4f,
                Mathf.Infinity,
                deltaTime);
        }

        private bool TryGetOcclusionProjection(
            Vector3 targetPosition,
            Vector3 referencePosition,
            Quaternion referenceRotation,
            out Vector3 targetViewport,
            out Rect ownshipRect)
        {
            targetViewport = default;
            ownshipRect = default;
            InitializeFieldOfView();
            RefreshOwnshipBounds();
            if (controlledCamera == null || !hasOwnshipWorldBounds)
            {
                return false;
            }

            var inverseRotation = Quaternion.Inverse(referenceRotation);
            compositionTargetCameraPosition = inverseRotation * (targetPosition - referencePosition);
            if (!IsFinite(compositionTargetCameraPosition) || compositionTargetCameraPosition.z <= 0.01f)
            {
                return false;
            }
            var heightPerDepth = 2f * Mathf.Tan(controlledCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            compositionProjectionScale = new Vector2(
                heightPerDepth * Mathf.Max(0.1f, controlledCamera.aspect), heightPerDepth);
            targetViewport = ProjectCameraPoint(compositionTargetCameraPosition);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (var i = 0; i < ownshipWorldCorners.Length; i++)
            {
                var corner = inverseRotation * (ownshipWorldCorners[i] - referencePosition);
                // An overlapping/behind-camera target has no well-defined
                // foreground/background escape. Avoid commanding an extreme jump.
                if (corner.z <= 0.01f || compositionTargetCameraPosition.z <= corner.z + 0.01f)
                    return false;
                ownshipCameraCorners[i] = corner;
                var viewport = ProjectCameraPoint(corner);
                min = Vector2.Min(min, viewport);
                max = Vector2.Max(max, viewport);
            }
            ownshipRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

        private Vector3 ProjectCameraPoint(Vector3 point) =>
            new Vector3(0.5f + point.x / (point.z * compositionProjectionScale.x),
                        0.5f + point.y / (point.z * compositionProjectionScale.y), point.z);

        private static Rect ExpandRect(Rect rect, float padding) =>
            Rect.MinMaxRect(rect.xMin - padding, rect.yMin - padding,
                           rect.xMax + padding, rect.yMax + padding);

        private void ResolveCombatReferences()
        {
            if (followTarget == null)
            {
                return;
            }

            if (radar == null)
            {
                radar = followTarget.GetComponent<MechRadarController>();
            }

            if (followRenderers.Length == 0)
            {
                followRenderers = followTarget.GetComponentsInChildren<Renderer>(true);
                nextBoundsRefreshTime = 0f;
            }
        }

        private void RefreshOwnshipBounds()
        {
            if (followTarget == null)
            {
                hasOwnshipWorldBounds = false;
                return;
            }

            if (!hasOwnshipLocalBounds || Time.unscaledTime >= nextBoundsRefreshTime)
            {
                RefreshOwnshipLocalBounds();
            }
            // Cache shape, never world position: the proxy follows translation,
            // rotation and scale every frame, even between shape refreshes.
            for (var i = 0; i < ownshipWorldCorners.Length; i++)
            {
                var corner = followTarget.TransformPoint(BoundsCorner(ownshipLocalBounds, i));
                ownshipWorldCorners[i] = corner;
                if (i == 0) ownshipWorldBounds = new Bounds(corner, Vector3.zero);
                else ownshipWorldBounds.Encapsulate(corner);
            }
            hasOwnshipWorldBounds = hasOwnshipLocalBounds;
        }

        private void RefreshOwnshipLocalBounds()
        {
            nextBoundsRefreshTime = Time.unscaledTime + Mathf.Max(0.02f, rendererBoundsRefreshSeconds);
            // Refresh the renderer list as well so rebuilding a loadout cannot
            // leave the camera measuring destroyed renderers indefinitely.
            followRenderers = followTarget.GetComponentsInChildren<Renderer>(true);
            hasOwnshipLocalBounds = false;
            var maximumRendererRadius = Mathf.Max(12f, distance * 2f);
            var maximumCenterDistance = Mathf.Max(12f, distance * 1.5f);
            var rootFromWorld = followTarget.worldToLocalMatrix;
            for (var i = 0; i < followRenderers.Length; i++)
            {
                var renderer = followRenderers[i];
                if (renderer == null ||
                    !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy ||
                    renderer is ParticleSystemRenderer ||
                    renderer is TrailRenderer ||
                    renderer is LineRenderer)
                {
                    continue;
                }

                var localBounds = renderer.localBounds;
                var worldFromRenderer = renderer.localToWorldMatrix;
                var worldCenter = worldFromRenderer.MultiplyPoint3x4(localBounds.center);
                var worldRadius = Mathf.Sqrt(
                    worldFromRenderer.MultiplyVector(Vector3.right * localBounds.extents.x).sqrMagnitude +
                    worldFromRenderer.MultiplyVector(Vector3.up * localBounds.extents.y).sqrMagnitude +
                    worldFromRenderer.MultiplyVector(Vector3.forward * localBounds.extents.z).sqrMagnitude);
                if (!IsFinite(worldCenter) || !IsFinite(localBounds.extents) ||
                    !float.IsFinite(worldRadius) || worldRadius > maximumRendererRadius ||
                    Vector3.Distance(worldCenter, followTarget.position) > maximumCenterDistance)
                {
                    continue;
                }

                var rootFromRenderer = rootFromWorld * worldFromRenderer;
                for (var cornerIndex = 0; cornerIndex < 8; cornerIndex++)
                {
                    var corner = rootFromRenderer.MultiplyPoint3x4(BoundsCorner(localBounds, cornerIndex));
                    if (!hasOwnshipLocalBounds)
                    {
                        ownshipLocalBounds = new Bounds(corner, Vector3.zero);
                        hasOwnshipLocalBounds = true;
                    }
                    else ownshipLocalBounds.Encapsulate(corner);
                }
            }

            if (!hasOwnshipLocalBounds)
            {
                var fallbackSize = new Vector3(
                    Mathf.Max(1f, fallbackOwnshipBoundsSize.x),
                    Mathf.Max(1f, fallbackOwnshipBoundsSize.y),
                    Mathf.Max(1f, fallbackOwnshipBoundsSize.z));
                var scale = followTarget.lossyScale;
                var localSize = new Vector3(
                    fallbackSize.x / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
                    fallbackSize.y / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
                    fallbackSize.z / Mathf.Max(0.0001f, Mathf.Abs(scale.z)));
                ownshipLocalBounds = new Bounds(Vector3.up * (localSize.y * 0.5f), localSize);
                hasOwnshipLocalBounds = true;
            }
        }

        private static Vector3 BoundsCorner(Bounds bounds, int index) =>
            bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((index & 1) == 0 ? -1f : 1f,
                            (index & 2) == 0 ? -1f : 1f,
                            (index & 4) == 0 ? -1f : 1f));

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsNaN(value.z) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y) &&
            !float.IsInfinity(value.z);

        private void SmoothLocalLag(Vector3 targetLag, float deltaTime)
        {
            var baseSmoothTime = 1f / Mathf.Max(0.01f, followResponse);
            localLag.x = Mathf.SmoothDamp(
                localLag.x,
                targetLag.x,
                ref localLagVelocity.x,
                baseSmoothTime * 1.4f,
                Mathf.Infinity,
                deltaTime);
            localLag.y = Mathf.SmoothDamp(
                localLag.y,
                targetLag.y,
                ref localLagVelocity.y,
                baseSmoothTime * 1.7f,
                Mathf.Infinity,
                deltaTime);
            localLag.z = Mathf.SmoothDamp(
                localLag.z,
                targetLag.z,
                ref localLagVelocity.z,
                baseSmoothTime * 2.4f,
                Mathf.Infinity,
                deltaTime);
        }

        private void UpdateFieldOfView(float deltaTime)
        {
            InitializeFieldOfView();
            if (controlledCamera == null)
            {
                return;
            }

            var speed01 = Mathf.Clamp01(motion.Velocity.magnitude / fullSpeedFovAt);
            speed01 = speed01 * speed01 * (3f - 2f * speed01);
            var targetFieldOfView = baseFieldOfView + maxSpeedFovIncrease * speed01;
            var keyboard = Keyboard.current;
            var zoomControl = keyboard != null ? keyboard[zoomKey] : null;
            var zoomActive = zoomControl?.isPressed == true;
            if (zoomActive)
            {
                var scroll = Mouse.current?.scroll.ReadValue().y ?? 0f;
                if (scroll > 0.01f)
                {
                    zoomStage = Mathf.Min(2, zoomStage + 1);
                }
                else if (scroll < -0.01f)
                {
                    zoomStage = Mathf.Max(0, zoomStage - 1);
                }

                var selectedFieldOfView = baseFieldOfView /
                    Mathf.Max(1f, ResolveZoomMagnification(zoomStage));
                targetFieldOfView = Mathf.Min(
                    targetFieldOfView,
                    selectedFieldOfView);
            }
            var ownshipVisualState = !zoomActive
                ? OwnshipZoomVisualState.Visible
                : zoomStage >= 1
                    ? OwnshipZoomVisualState.Hidden
                    : OwnshipZoomVisualState.Ghost;
            SetOwnshipZoomVisualState(ownshipVisualState);
            controlledCamera.fieldOfView = Mathf.SmoothDamp(
                controlledCamera.fieldOfView,
                targetFieldOfView,
                ref fieldOfViewVelocity,
                zoomActive ? 0.12f : 0.24f,
                Mathf.Infinity,
                deltaTime);
        }

        private void SetOwnshipZoomVisualState(OwnshipZoomVisualState state)
        {
            if (state == ownshipZoomVisualState)
            {
                return;
            }

            if (state != OwnshipZoomVisualState.Visible &&
                zoomRenderers.Length == 0)
            {
                if (followTarget == null)
                {
                    return;
                }

                var candidates = followTarget.GetComponentsInChildren<Renderer>(true);
                var count = 0;
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (candidates[i] is MeshRenderer or SkinnedMeshRenderer)
                    {
                        count++;
                    }
                }

                zoomRenderers = new Renderer[count];
                zoomRendererPreviousStates = new bool[count];
                zoomRendererPreviousMaterials = new Material[count][];
                var index = 0;
                for (var i = 0; i < candidates.Length; i++)
                {
                    var renderer = candidates[i];
                    if (renderer is not MeshRenderer &&
                        renderer is not SkinnedMeshRenderer)
                    {
                        continue;
                    }

                    zoomRenderers[index] = renderer;
                    zoomRendererPreviousStates[index] = renderer.forceRenderingOff;
                    zoomRendererPreviousMaterials[index] = renderer.sharedMaterials;
                    index++;
                }
            }

            if (state == OwnshipZoomVisualState.Visible)
            {
                RestoreOwnshipRenderers();
                ownshipZoomVisualState = state;
                return;
            }

            if (state == OwnshipZoomVisualState.Ghost)
            {
                EnsureZoomGhostMaterial();
            }

            for (var i = 0; i < zoomRenderers.Length; i++)
            {
                var renderer = zoomRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (state == OwnshipZoomVisualState.Hidden)
                {
                    renderer.forceRenderingOff = true;
                    continue;
                }

                renderer.forceRenderingOff =
                    i < zoomRendererPreviousStates.Length &&
                    zoomRendererPreviousStates[i];
                var originalMaterials =
                    i < zoomRendererPreviousMaterials.Length
                        ? zoomRendererPreviousMaterials[i]
                        : null;
                if (zoomGhostMaterial != null &&
                    originalMaterials is { Length: > 0 })
                {
                    var ghostMaterials = new Material[originalMaterials.Length];
                    for (var materialIndex = 0;
                         materialIndex < ghostMaterials.Length;
                         materialIndex++)
                    {
                        ghostMaterials[materialIndex] = zoomGhostMaterial;
                    }
                    renderer.sharedMaterials = ghostMaterials;
                }
            }

            ownshipZoomVisualState = state;
        }

        private void EnsureZoomGhostMaterial()
        {
            if (zoomGhostMaterial != null)
            {
                zoomGhostMaterial.SetFloat(
                    "_GhostAlpha",
                    overviewZoomOwnshipAlpha);
                return;
            }

            var shader = Shader.Find("ACunicombat/Zoom Dotted Ghost");
            if (shader == null)
            {
                return;
            }

            zoomGhostMaterial = new Material(shader)
            {
                name = "Runtime Zoom Dotted Ghost",
                hideFlags = HideFlags.HideAndDontSave,
            };
            zoomGhostMaterial.SetFloat("_GhostAlpha", overviewZoomOwnshipAlpha);
        }

        private void RestoreOwnshipRenderers()
        {
            for (var i = 0; i < zoomRenderers.Length; i++)
            {
                var renderer = zoomRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                renderer.forceRenderingOff =
                    i < zoomRendererPreviousStates.Length &&
                    zoomRendererPreviousStates[i];
                if (i < zoomRendererPreviousMaterials.Length &&
                    zoomRendererPreviousMaterials[i] != null)
                {
                    renderer.sharedMaterials =
                        zoomRendererPreviousMaterials[i];
                }
            }

            zoomRenderers = System.Array.Empty<Renderer>();
            zoomRendererPreviousStates = System.Array.Empty<bool>();
            zoomRendererPreviousMaterials = System.Array.Empty<Material[]>();
        }

        private void OnDisable()
        {
            SetOwnshipZoomVisualState(OwnshipZoomVisualState.Visible);
        }

        private void OnDestroy()
        {
            RestoreOwnshipRenderers();
            if (zoomGhostMaterial != null)
            {
                Destroy(zoomGhostMaterial);
                zoomGhostMaterial = null;
            }
        }

        private enum OwnshipZoomVisualState
        {
            Visible,
            Ghost,
            Hidden,
        }

        private void InitializeFieldOfView()
        {
            if (fieldOfViewInitialized)
            {
                return;
            }

            controlledCamera = GetComponent<Camera>();
            if (controlledCamera == null)
            {
                return;
            }

            ApplyClipPlanes();
            baseFieldOfView = controlledCamera.fieldOfView;
            fieldOfViewInitialized = true;
        }

        private float ResolveZoomMagnification(int stage)
        {
            var maximumMagnification = 20f;
            if (followTarget != null)
            {
                var state = followTarget.GetComponent<MechRuntimeState>();
                var configured = state?.AggregatedStats?.headSensorPerformance?.maximumMagnification ?? 0f;
                if (configured > 0f)
                {
                    maximumMagnification = configured;
                }
            }

            return stage switch
            {
                2 => Mathf.Max(1f, maximumMagnification),
                1 => Mathf.Min(10f, Mathf.Max(1f, maximumMagnification)),
                _ => Mathf.Min(3f, Mathf.Max(1f, maximumMagnification)),
            };
        }

        private void ApplyClipPlanes()
        {
            if (controlledCamera == null)
            {
                controlledCamera = GetComponent<Camera>();
            }

            if (controlledCamera == null)
            {
                return;
            }

            controlledCamera.nearClipPlane = Mathf.Max(0.01f, nearClipDistance);
            controlledCamera.farClipPlane = Mathf.Max(
                controlledCamera.nearClipPlane + 1f,
                farClipDistance);
        }

        private static float SoftLimit(float value, float limit)
        {
            if (limit <= 0f || Mathf.Approximately(value, 0f))
            {
                return 0f;
            }

            return Mathf.Sign(value) * limit * (1f - Mathf.Exp(-Mathf.Abs(value) / limit));
        }
    }
}
