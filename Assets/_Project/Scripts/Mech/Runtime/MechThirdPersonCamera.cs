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
        // 旧实现只给固定横移。当前约 27.7 m 机距下，机体张角仍接近缩放前的构图；要把远处目标从剪影
        // 中心推到边缘需要约 20*tan(19 度) = 6.9 m，固定 3 m 不到需要量的一半，必然推不出去。
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
        private Bounds ownshipWorldBounds;
        private bool hasOwnshipWorldBounds;
        private float nextBoundsRefreshTime;
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
        private bool occlusionEscapeClamped;
        private float occlusionEscapeSwitchAllowedAt;
        private float occlusionHoldUntil;
        private bool freeLookActive;
        private float freeLookYawDegrees;
        private float freeLookPitchDegrees;
        private float freeLookYawVelocity;
        private float freeLookPitchVelocity;

        public float PitchDegrees => pitchDegrees;
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
            var origin = followTarget.position + motion.ReferenceUp * focusHeight;
            return new Ray(origin, aimRotation * localDirection);
        }

        public void Configure(Transform target, MechFlightMotionController motionController)
        {
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
            compositionTrackId = default;
            zoomTrackControlFilter.Reset();
            compositionTrackControlFilter.Reset();
            shoulderBiasUntil = 0f;
            occlusionEscapeAxis = -1;
            occlusionEscapeMeters = 0f;
            occlusionEscapeClamped = false;
            occlusionHoldUntil = 0f;
            occlusionEscapeSwitchAllowedAt = 0f;
            fieldOfViewVelocity = 0f;
            ResetFreeLookImmediate();
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
            if (motion == null)
            {
                return;
            }

            motion.SetAimPitch(0f);
            motion.SetYawTurnInput(0f);
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
                return;
            }

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
            motion.SetYawTurnInput(
                ApplyDeadZone(normalizedOffset.x, aimDeadZone));

            var pitchInput =
                ApplyDeadZone(normalizedOffset.y, aimDeadZone);
            pitchDegrees = motion.ConstrainPitchForCurrentMode(
                pitchDegrees -
                pitchInput * pitchTurnRateDegrees * Time.unscaledDeltaTime);
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
            motion.SetYawTurnInput(0f);
        }

        private void EndFreeLook()
        {
            if (!freeLookActive)
            {
                return;
            }

            freeLookActive = false;
            motion?.SetYawTurnInput(0f);
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

            var focusPoint = followTarget.position + motion.ReferenceUp * focusHeight;
            var direction = StabilizeTrackPosition(
                                ref zoomTrackControlFilter,
                                in sttTrack,
                                Time.unscaledDeltaTime) -
                            focusPoint;
            if (direction.sqrMagnitude <= 0.001f)
            {
                return false;
            }

            var localDirection = Quaternion.Inverse(motion.ReferenceRotation) *
                direction.normalized;
            var desiredYaw = Mathf.Atan2(localDirection.x, localDirection.z) *
                Mathf.Rad2Deg;
            var horizontal = Mathf.Sqrt(
                localDirection.x * localDirection.x +
                localDirection.z * localDirection.z);
            var desiredPitch = -Mathf.Atan2(localDirection.y, horizontal) *
                Mathf.Rad2Deg;
            var yawError = Mathf.DeltaAngle(motion.LogicalYawDegrees, desiredYaw);
            motion.SetYawTurnInput(Mathf.Clamp(
                yawError / Mathf.Max(1f, zoomTrackingYawCaptureDegrees),
                -1f,
                1f));
            pitchDegrees = motion.ConstrainPitchForCurrentMode(
                Mathf.MoveTowardsAngle(
                    pitchDegrees,
                    desiredPitch,
                    Mathf.Max(1f, zoomTrackingPitchRateDegrees) *
                    Time.unscaledDeltaTime));
            aimViewportPoint = Vector2.MoveTowards(
                aimViewportPoint,
                aimViewportCenter,
                Time.unscaledDeltaTime * 2.5f);
            return true;
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
            var targetComposition = HasFreeLookOffset
                ? BaseCompositionOffset()
                : ComputeTargetCompositionOffset();
            SmoothCompositionOffset(targetComposition, deltaTime);
            transform.SetPositionAndRotation(
                desiredPosition + rotation * (localLag + compositionLocalOffset),
                rotation);
            UpdateFieldOfView(deltaTime);
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
            var aimRotation = ComputeAimRotation();
            var yawedRotation =
                Quaternion.AngleAxis(freeLookYawDegrees, motion.ReferenceUp) *
                aimRotation;
            return Quaternion.AngleAxis(
                       freeLookPitchDegrees,
                       yawedRotation * Vector3.right) *
                   yawedRotation;
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
            var focusPoint = followTarget.position + motion.ReferenceUp * focusHeight;
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

        private Vector3 ComputeTargetCompositionOffset()
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
                if (!sttTrack.TrackId.Equals(compositionTrackId))
                {
                    // 换目标时清掉避让状态，避免沿用上一个目标的绕行方向。
                    compositionTrackId = sttTrack.TrackId;
                    occlusionEscapeAxis = -1;
                    occlusionEscapeSwitchAllowedAt = 0f;
                }

                shoulderBiasUntil = now + Mathf.Max(0.5f, shoulderReleaseSeconds);
                targetOffset.x = shoulderSign * sttCruiseShoulderOffset;
            }
            else
            {
                compositionTrackId = default;
                if (now < shoulderBiasUntil)
                {
                    targetOffset.x = shoulderSign * sttCruiseShoulderOffset;
                }
            }

            if (!TryResolveOcclusionTarget(hasStt, sttTrack, out var avoidTrack))
            {
                compositionTrackControlFilter.Reset();
                occlusionEscapeAxis = -1;
                occlusionEscapeMeters = 0f;
                occlusionEscapeClamped = false;
                return targetOffset;
            }

            ApplyOcclusionEscape(
                StabilizeTrackPosition(
                    ref compositionTrackControlFilter,
                    in avoidTrack,
                    Time.unscaledDeltaTime),
                now,
                ref targetOffset);
            return targetOffset;
        }

        /// <summary>
        /// 遮挡避让的目标：STT → 准星候选 → 最优航迹兜底。
        /// 旧实现只认 STT，未锁定时 <c>IsTargetOccludedByOwnship</c> 一次都不会被调用——
        /// 这是"机体完全盖住目标"最主要的成因。
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
            ref Vector3 targetOffset)
        {
            var occluded = IsTargetOccludedByOwnship(
                targetPosition,
                out var targetViewport,
                out var ownshipRect);
            if (occluded)
            {
                occlusionHoldUntil = now + Mathf.Max(0.1f, occlusionHoldSeconds);
                UpdateEscapeDecision(targetViewport, ownshipRect, now);
            }
            else if (now >= occlusionHoldUntil)
            {
                // 目标露出后再保持一小段：剪影边缘附近的目标否则会让镜头反复进出避让状态。
                occlusionEscapeAxis = -1;
                occlusionEscapeMeters = 0f;
                occlusionEscapeClamped = false;
            }

            if (occlusionEscapeAxis == 0)
            {
                // 与巡航肩位同轴：取两者中较大的位移，避免叠加成两倍。
                targetOffset.x = occlusionEscapeSign * Mathf.Max(
                    Mathf.Abs(targetOffset.x),
                    occlusionEscapeMeters);
                shoulderSign = occlusionEscapeSign;
            }
            else if (occlusionEscapeAxis == 1)
            {
                targetOffset.y += occlusionEscapeSign * occlusionEscapeMeters;
            }

            // 单独后撤解决不了遮挡（机体与目标同时缩小、屏幕相对位置不变），但位移已经顶到
            // 上限仍未露出时，后撤能减小剪影角宽度从而降低所需位移，作为辅助是有意义的。
            if (occluded && occlusionEscapeClamped)
            {
                targetOffset.z -= sttOcclusionPullback;
            }
        }

        /// <summary>
        /// 闭环选路：把目标推出剪影所需的视口位移换算成相机位移，在左/右/上/下四个方向里
        /// 取代价最小的一个。相机只平移不转向，而机体在当前跟随距离、目标在数公里外，视差使得
        /// 横移主要移动机体、几乎不移动目标——这正是位移能分离两者的原因。
        /// </summary>
        private void UpdateEscapeDecision(
            Vector3 targetViewport,
            Rect ownshipRect,
            float now)
        {
            if (!TryGetViewportScale(out var lateralPerViewport, out var verticalPerViewport))
            {
                return;
            }

            var margin = occlusionEscapeMargin;
            // 相机 +x 使近处的机体在画面上左移，所以"把机体推到目标左侧"对应 sign = +1。
            var pushLeft = (ownshipRect.xMax - targetViewport.x + margin) * lateralPerViewport;
            var pushRight = (targetViewport.x - ownshipRect.xMin + margin) * lateralPerViewport;
            var pushDown = (ownshipRect.yMax - targetViewport.y + margin) * verticalPerViewport;
            var pushUp = (targetViewport.y - ownshipRect.yMin + margin) * verticalPerViewport;

            var bestAxis = 0;
            var bestSign = 1f;
            var bestCost = pushLeft;
            if (pushRight < bestCost)
            {
                bestAxis = 0;
                bestSign = -1f;
                bestCost = pushRight;
            }
            if (pushDown < bestCost)
            {
                bestAxis = 1;
                bestSign = 1f;
                bestCost = pushDown;
            }
            if (pushUp < bestCost)
            {
                bestAxis = 1;
                bestSign = -1f;
                bestCost = pushUp;
            }

            // 迟滞：同轴同向时允许连续跟随重叠深度调整（这才是"闭环"）；换边/换轴需要新方案
            // 明显更省且冷却已过。旧实现用的是 2.5 s 硬锁，目标横穿剪影时会一直被挡住。
            if (occlusionEscapeAxis >= 0)
            {
                var currentCost = ResolveEscapeCost(
                    occlusionEscapeAxis,
                    occlusionEscapeSign,
                    pushLeft,
                    pushRight,
                    pushDown,
                    pushUp);
                var sameChoice =
                    occlusionEscapeAxis == bestAxis &&
                    Mathf.Approximately(occlusionEscapeSign, bestSign);
                if (!sameChoice)
                {
                    var cheaperByMargin = bestCost + occlusionEscapeSwitchMargin < currentCost;
                    if (!cheaperByMargin || now < occlusionEscapeSwitchAllowedAt)
                    {
                        bestAxis = occlusionEscapeAxis;
                        bestSign = occlusionEscapeSign;
                        bestCost = currentCost;
                    }
                    else
                    {
                        occlusionEscapeSwitchAllowedAt =
                            now + Mathf.Max(0f, occlusionEscapeSwitchCooldown);
                    }
                }
            }

            var limit = bestAxis == 0 ? occlusionLateralLimit : occlusionVerticalLimit;
            occlusionEscapeClamped = bestCost > limit;
            occlusionEscapeAxis = bestAxis;
            occlusionEscapeSign = bestSign;
            occlusionEscapeMeters = Mathf.Min(Mathf.Max(0f, bestCost), limit);
        }

        private static float ResolveEscapeCost(
            int axis,
            float sign,
            float pushLeft,
            float pushRight,
            float pushDown,
            float pushUp)
        {
            if (axis == 0)
            {
                return sign >= 0f ? pushLeft : pushRight;
            }

            return sign >= 0f ? pushDown : pushUp;
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

        /// <summary>
        /// 一个完整视口跨度在机体所在深度上对应多少米。视口位移必须按<b>机体</b>的距离换算，
        /// 因为要移动的是机体在画面上的位置，目标在数公里外基本不动。
        /// </summary>
        private bool TryGetViewportScale(
            out float lateralPerViewport,
            out float verticalPerViewport)
        {
            lateralPerViewport = 0f;
            verticalPerViewport = 0f;
            if (controlledCamera == null || !hasOwnshipWorldBounds)
            {
                return false;
            }

            var ownshipDistance = Vector3.Distance(
                transform.position,
                ownshipWorldBounds.center);
            if (ownshipDistance <= 0.01f)
            {
                return false;
            }

            var halfFovRadians = controlledCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            verticalPerViewport = 2f * Mathf.Tan(halfFovRadians) * ownshipDistance;
            lateralPerViewport = verticalPerViewport * Mathf.Max(0.1f, controlledCamera.aspect);
            return verticalPerViewport > 0.0001f;
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

        private bool IsTargetOccludedByOwnship(
            Vector3 targetPosition,
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

            targetViewport = controlledCamera.WorldToViewportPoint(targetPosition);
            var ownshipCenterViewport =
                controlledCamera.WorldToViewportPoint(ownshipWorldBounds.center);
            if (targetViewport.z <= 0f ||
                ownshipCenterViewport.z <= 0f ||
                targetViewport.z <= ownshipCenterViewport.z ||
                !TryProjectBoundsToViewport(ownshipWorldBounds, out ownshipRect))
            {
                return false;
            }

            ownshipRect.xMin -= occlusionViewportPadding;
            ownshipRect.xMax += occlusionViewportPadding;
            ownshipRect.yMin -= occlusionViewportPadding;
            ownshipRect.yMax += occlusionViewportPadding;
            return ownshipRect.Contains(new Vector2(targetViewport.x, targetViewport.y));
        }

        private bool TryProjectBoundsToViewport(Bounds bounds, out Rect viewportRect)
        {
            viewportRect = default;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var projectedCorners = 0;
            for (var x = -1; x <= 1; x += 2)
            {
                for (var y = -1; y <= 1; y += 2)
                {
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var corner = bounds.center + Vector3.Scale(
                            bounds.extents,
                            new Vector3(x, y, z));
                        var viewport = controlledCamera.WorldToViewportPoint(corner);
                        if (viewport.z <= 0f)
                        {
                            continue;
                        }

                        min = Vector2.Min(min, viewport);
                        max = Vector2.Max(max, viewport);
                        projectedCorners++;
                    }
                }
            }

            if (projectedCorners == 0)
            {
                return false;
            }

            viewportRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

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
            if (Time.unscaledTime < nextBoundsRefreshTime)
            {
                return;
            }

            nextBoundsRefreshTime =
                Time.unscaledTime + Mathf.Max(0.02f, rendererBoundsRefreshSeconds);
            hasOwnshipWorldBounds = false;
            var maximumRendererRadius = Mathf.Max(12f, distance * 2f);
            var maximumCenterDistance = Mathf.Max(12f, distance * 1.5f);
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

                var rendererBounds = renderer.bounds;
                if (!IsFinite(rendererBounds.center) ||
                    !IsFinite(rendererBounds.extents) ||
                    rendererBounds.extents.magnitude > maximumRendererRadius ||
                    Vector3.Distance(rendererBounds.center, followTarget.position) >
                    maximumCenterDistance)
                {
                    continue;
                }

                if (!hasOwnshipWorldBounds)
                {
                    ownshipWorldBounds = rendererBounds;
                    hasOwnshipWorldBounds = true;
                }
                else
                {
                    ownshipWorldBounds.Encapsulate(rendererBounds);
                }
            }

            if (!hasOwnshipWorldBounds)
            {
                var fallbackSize = new Vector3(
                    Mathf.Max(1f, fallbackOwnshipBoundsSize.x),
                    Mathf.Max(1f, fallbackOwnshipBoundsSize.y),
                    Mathf.Max(1f, fallbackOwnshipBoundsSize.z));
                ownshipWorldBounds = new Bounds(
                    followTarget.position +
                    (motion != null ? motion.ReferenceUp : Vector3.up) *
                    (fallbackSize.y * 0.5f),
                    fallbackSize);
                hasOwnshipWorldBounds = true;
            }
        }

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
