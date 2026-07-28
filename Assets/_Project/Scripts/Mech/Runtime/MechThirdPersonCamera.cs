using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 第三人称镜头：初始在机体后方看向机体前方；水平鼠标 = Yaw（同时是机体转向来源，
    /// 由 MotionController 持有同一 Yaw 值）；垂直鼠标 = Pitch（仅镜头/瞄准，不给机体）；
    /// 无 Roll。镜头前方、机体逻辑前方、推进前方、瞄准前方共用 Yaw 保持一致。
    /// </summary>
    public sealed class MechThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] private Transform followTarget;
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private float distance = 20f;
        [SerializeField] private float focusHeight = 7.5f;
        [SerializeField] private float pitchSensitivity = 0.12f;
        [SerializeField, Min(0.01f)] private float followResponse = 8f;
        [Header("High-speed framing")]
        [SerializeField, Min(0.01f)] private float maxHorizontalLagRatio = 0.26f;
        [SerializeField, Min(0.01f)] private float maxVerticalLagRatio = 0.18f;
        [SerializeField, Min(0.01f)] private float maxDepthLagRatio = 0.45f;
        [SerializeField, Min(0.001f)] private float velocityLookbackSeconds = 0.075f;
        [SerializeField, Min(0f)] private float maxSpeedFovIncrease = 7f;
        [SerializeField, Min(1f)] private float fullSpeedFovAt = 350f;
        [Header("Combat composition")]
        [SerializeField, Min(0f)] private float baseCameraHeightOffset = 3.6f;
        [SerializeField, Min(0f)] private float baseDistanceOffset = 3f;
        [SerializeField, Min(0f)] private float sttCruiseShoulderOffset = 3f;
        [SerializeField, Min(0f)] private float sttOcclusionPullback = 2.5f;
        [SerializeField, Min(0.01f)] private float compositionResponse = 5f;
        [SerializeField, Min(0f)] private float shoulderHoldSeconds = 3f;
        [SerializeField, Min(0f)] private float shoulderReleaseSeconds = 1.5f;
        [SerializeField, Range(0f, 0.2f)] private float shoulderSwitchDeadZone = 0.06f;
        [SerializeField, Range(0f, 0.2f)] private float occlusionViewportPadding = 0.025f;
        [SerializeField, Min(0.02f)] private float rendererBoundsRefreshSeconds = 0.12f;
        [SerializeField] private Vector3 fallbackOwnshipBoundsSize = new Vector3(14f, 18f, 10f);
        [Header("Mouse aim cursor")]
        [SerializeField] private Vector2 aimViewportCenter = new Vector2(0.5f, 0.56f);
        [SerializeField] private Vector2 aimCursorHalfExtent = new Vector2(0.30f, 0.25f);
        [SerializeField, Min(0.01f)] private float aimCursorSensitivity = 1f;
        [SerializeField, Range(0f, 0.5f)] private float aimDeadZone = 0.06f;
        [SerializeField, Min(1f)] private float pitchTurnRateDegrees = 60f;
        [Header("Space-scale rendering")]
        [SerializeField, Min(0.01f)] private float nearClipDistance = 0.5f;
        [SerializeField, Min(1000f)] private float farClipDistance = 100000f;
        [Header("View zoom")]
        [SerializeField] private Key zoomKey = Key.CapsLock;
        [SerializeField, Range(5f, 60f)] private float zoomFieldOfView = 22f;
        [SerializeField, Range(3f, 20f)] private float precisionZoomFieldOfView = 6.6f;
        [SerializeField, Range(2f, 12f)] private float maximumZoomFieldOfView = 3.3f;
        [SerializeField, Range(0.05f, 0.8f)] private float overviewZoomOwnshipAlpha = 0.24f;
        [SerializeField, Range(0.05f, 1f)] private float overviewZoomAimSensitivity = 0.55f;
        [SerializeField, Range(0.02f, 1f)] private float precisionZoomAimSensitivity = 0.24f;
        [SerializeField, Range(0.01f, 1f)] private float maximumZoomAimSensitivity = 0.13f;

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
        private float shoulderDecisionLockedUntil;
        private float shoulderAvoidanceUntil;
        private float shoulderBiasUntil;
        private uint observedReferenceFrameVersion;

        public float PitchDegrees => pitchDegrees;
        public Vector2 AimViewportPoint
        {
            get
            {
                EnsureAimCursorInitialized();
                return aimViewportPoint;
            }
        }

        public Quaternion AimRotation => ComputeRotation();
        public bool IsZoomActive
        {
            get
            {
                var keyboard = Keyboard.current;
                return keyboard != null &&
                       keyboard[zoomKey]?.isPressed == true;
            }
        }

        public int ZoomMagnification => zoomStage switch
        {
            2 => 20,
            1 => 10,
            _ => 3,
        };

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
            shoulderDecisionLockedUntil = 0f;
            shoulderAvoidanceUntil = 0f;
            shoulderBiasUntil = 0f;
            fieldOfViewVelocity = 0f;
            var rotation = ComputeRotation();
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

            EnsureAimCursorInitialized();
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

        public void Follow(float deltaTime)
        {
            if (followTarget == null || motion == null)
            {
                return;
            }

            if (observedReferenceFrameVersion != motion.ReferenceFrameVersion)
            {
                ResetToReferenceHorizon();
            }

            var rotation = ComputeRotation();
            var desiredPosition = ComputePosition(rotation);
            var targetLag = ComputeTargetLocalLag(rotation);
            SmoothLocalLag(targetLag, deltaTime);
            var targetComposition = ComputeTargetCompositionOffset();
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

        private Quaternion ComputeRotation() =>
            motion.ResolveReferenceViewRotation(pitchDegrees);

        private Vector3 ComputePosition() => ComputePosition(ComputeRotation());

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
            if (radar == null ||
                !radar.SttTrackId.IsValid ||
                !radar.TryGetTrack(radar.SttTrackId, out var track))
            {
                compositionTrackId = default;
                if (now < shoulderBiasUntil)
                {
                    targetOffset.x =
                        shoulderSign * sttCruiseShoulderOffset;
                }

                return targetOffset;
            }

            var sttTrackId = track.TrackId;
            var isNewTrack = !sttTrackId.Equals(compositionTrackId);
            if (!sttTrackId.Equals(compositionTrackId))
            {
                compositionTrackId = sttTrackId;
                shoulderDecisionLockedUntil = 0f;
                shoulderAvoidanceUntil = 0f;
            }

            var targetPosition = radar.PredictTrackPosition(track);
            var occluded = IsTargetOccludedByOwnship(
                targetPosition,
                out var targetViewport,
                out var ownshipRect);
            var hasCompositionProjection =
                targetViewport.z > 0f &&
                ownshipRect.width > 0f;
            var horizontalDelta = hasCompositionProjection
                ? targetViewport.x - ownshipRect.center.x
                : 0f;
            var holdDuration = Mathf.Max(2.5f, shoulderHoldSeconds);
            if ((isNewTrack || occluded) &&
                now >= shoulderDecisionLockedUntil)
            {
                if (Mathf.Abs(horizontalDelta) >= shoulderSwitchDeadZone)
                {
                    shoulderSign = Mathf.Sign(horizontalDelta);
                }
                else if (Mathf.Abs(compositionLocalOffset.x) >= 0.1f)
                {
                    shoulderSign = Mathf.Sign(compositionLocalOffset.x);
                }

                shoulderDecisionLockedUntil = now + holdDuration;
            }

            shoulderBiasUntil =
                now + Mathf.Max(0.5f, shoulderReleaseSeconds);
            targetOffset.x = shoulderSign * sttCruiseShoulderOffset;
            if (occluded)
            {
                targetOffset.z -= sttOcclusionPullback;
                shoulderAvoidanceUntil = now + holdDuration;
            }
            else if (now < shoulderAvoidanceUntil)
            {
                // Keep the selected shoulder after the target clears the silhouette. The offset
                // remains fixed; only its persistence changes.
                targetOffset.x = shoulderSign * sttCruiseShoulderOffset;
            }

            return targetOffset;
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

                var selectedFieldOfView = zoomStage switch
                {
                    2 => maximumZoomFieldOfView,
                    1 => precisionZoomFieldOfView,
                    _ => zoomFieldOfView,
                };
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
