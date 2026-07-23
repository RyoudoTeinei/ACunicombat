using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Bridges scene truth into the deterministic sensor simulation and exposes only measurements,
    /// tracks, radar status and fire-control products to gameplay/UI consumers.
    /// </summary>
    [DefaultExecutionOrder(-1100)]
    [DisallowMultipleComponent]
    public sealed class MechRadarController : MonoBehaviour
    {
        public const int MaximumPublicTracks = 32;
        public const int MaximumPublicEchoes = 64;

        private const int MaximumSceneTargets = 16;
        private const int GuidanceChannelCapacity = 2;
        private const float SearchIntervalSeconds = 0.04f;
        private const float SttIntervalSeconds = 0.035f;
        private const float SearchAzimuthDegrees = 100f;
        private const float SearchElevationDegrees = 60f;
        private const float SearchBeamWidthDegrees = 12f;
        private const float SearchBeamSpacingDegrees = 9f;
        private const int SearchDwellsPerTick = 4;
        private const int SttSearchDwellsPerTick = 2;
        private const int TwsRevisitsPerTick = 2;
        private const float TwsRevisitIntervalSeconds = 0.24f;
        private const float TwsBeamWidthDegrees = 8f;
        private const float SttBeamWidthDegrees = 6f;
        private const float RadarRangeMeters = 100_000f;
        private const float DesignationBaseSeconds = 0.3f;
        private const float DesignationGimbalDegrees = 48f;

        private struct GuidanceChannel
        {
            public TrackId TrackId;
            public int ReferenceCount;
        }

        private SimulationDriver driver;
        private Camera aimingCamera;
        private CombatTestTarget configuredTarget;
        private SimEntityId playerEntity;
        private readonly CombatTestTarget[] sceneTargets = new CombatTestTarget[MaximumSceneTargets];
        private readonly SimEntityId[] targetEntities = new SimEntityId[MaximumSceneTargets];
        private readonly Vector3[] previousTargetPositions = new Vector3[MaximumSceneTargets];
        private readonly bool[] targetRadarRegistered = new bool[MaximumSceneTargets];
        private int sceneTargetCount;
        private readonly SensorId sensorId = new SensorId(new Handle(0, 1));
        private readonly TrackSnapshot[] trackScratch = new TrackSnapshot[MaximumPublicTracks];
        private readonly Measurement[] measurementScratch = new Measurement[MaximumPublicEchoes];
        private readonly GuidanceChannel[] guidanceChannels =
            new GuidanceChannel[GuidanceChannelCapacity];
        private readonly double[] nextTwsRevisitTimes =
            new double[MaximumPublicTracks];
        private readonly uint[] twsRevisitTrackKeys =
            new uint[MaximumPublicTracks];
        private readonly RasterScanPattern searchPattern = new RasterScanPattern(
            SearchAzimuthDegrees,
            SearchElevationDegrees,
            SearchBeamSpacingDegrees);

        private Vector3 previousPlayerPosition;
        private Vector3 playerVelocity;
        private bool initialized;
        private double nextSearchDwellTime;
        private double nextSttDwellTime;
        private ulong dwellSequence;
        private TrackId candidateTrackId;
        private TrackId sttTrackId;
        private float designationProgress;
        private bool trackOperationMode;
        private float beamAzimuthDegrees;
        private float beamElevationDegrees;
        private Vector3 searchFrameForward;
        private Vector3 searchFrameRight;
        private Vector3 searchFrameUp;

        public double SimTimeSeconds => driver != null ? driver.SimTimeSeconds : Time.timeAsDouble;
        public int DetectionCount => driver != null ? driver.DetectionCount : 0;
        public int TrackCount => driver != null ? driver.TrackCount : 0;
        public bool TrackOperationMode => trackOperationMode;
        public TrackId CandidateTrackId => candidateTrackId;
        public TrackId SttTrackId => sttTrackId;
        public bool HasStt => sttTrackId.IsValid && TryGetTrack(sttTrackId, out _);
        public float DesignationProgress => designationProgress;

        public void Configure(Camera newAimingCamera, CombatTestTarget newTarget)
        {
            aimingCamera = newAimingCamera;
            configuredTarget = newTarget;
            if (Application.isPlaying)
            {
                EnsureInitialized();
            }
        }

        public void SetTrackOperationMode(bool enabled)
        {
            trackOperationMode = enabled;
            if (!enabled)
            {
                candidateTrackId = default;
                designationProgress = 0f;
            }
        }

        public void BreakSingleTargetTrack()
        {
            sttTrackId = default;
            candidateTrackId = default;
            designationProgress = 0f;
        }

        public int CopyTracks(TrackSnapshot[] destination) =>
            driver != null ? driver.CopyTracks(destination) : 0;

        public int CopyEchoes(RadarEchoSnapshot[] destination)
        {
            if (destination == null || driver == null)
            {
                return 0;
            }

            var count = driver.CopyDetections(measurementScratch);
            count = Mathf.Min(count, destination.Length);
            for (var i = 0; i < count; i++)
            {
                var measurement = measurementScratch[i];
                destination[i] = new RadarEchoSnapshot
                {
                    EstimatedPosition = ToDouble3(transform.position)
                                        + new double3(
                                            measurement.Direction.x,
                                            measurement.Direction.y,
                                            measurement.Direction.z) * measurement.Range,
                    Timestamp = measurement.Timestamp,
                    SnrDb = measurement.SnrDb,
                    Flags = measurement.Flags,
                    Source = (measurement.Flags & MeasurementFlags.Passive) != 0
                        ? TrackSourceMask.Esm
                        : TrackSourceMask.Radar,
                };
            }

            return count;
        }

        public RadarStatusSnapshot GetStatusSnapshot()
        {
            var flags = RadarStatusFlags.None;
            var count = CopyTracks(trackScratch);
            var hasTwsTrack = false;
            for (var i = 0; i < count; i++)
            {
                if (trackScratch[i].Lifecycle == TrackLifecycle.Confirmed ||
                    trackScratch[i].Lifecycle == TrackLifecycle.Coasting ||
                    trackScratch[i].Lifecycle == TrackLifecycle.Degraded)
                {
                    hasTwsTrack = true;
                }

                if (trackScratch[i].InterferenceScore > 0.25f)
                {
                    flags |= RadarStatusFlags.Jammed;
                }

                if (trackScratch[i].Lifecycle == TrackLifecycle.Degraded)
                {
                    flags |= RadarStatusFlags.TrackDegraded;
                }
            }

            var scheduledDwells = driver != null ? driver.ScheduledDwellCount : 0;
            if (scheduledDwells > 8)
            {
                flags |= RadarStatusFlags.Overloaded;
            }

            var usedChannels = 0;
            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount > 0)
                {
                    usedChannels++;
                }
            }

            var hasStt = HasStt;
            return new RadarStatusSnapshot
            {
                Mode = hasStt
                    ? RadarOperatingMode.SingleTargetTrack
                    : hasTwsTrack
                        ? RadarOperatingMode.TrackWhileScan
                        : RadarOperatingMode.Search,
                RangeMeters = RadarRangeMeters,
                ScanAzimuthDegrees = SearchAzimuthDegrees,
                ScanElevationDegrees = SearchElevationDegrees,
                BeamAzimuthDegrees = beamAzimuthDegrees,
                BeamElevationDegrees = beamElevationDegrees,
                TrackCount = count,
                TrackCapacity = driver != null ? driver.TrackCapacity : MaximumPublicTracks,
                RefreshRateHz = GetSearchFrameRate(hasStt),
                DwellLoad = Mathf.Clamp01((scheduledDwells + (hasStt ? 5f : 6f)) / 16f),
                DeferredDwellCount = driver != null ? driver.DeferredDwellCount : 0,
                GuidanceChannelsUsed = usedChannels,
                GuidanceChannelCapacity = GuidanceChannelCapacity,
                OwnshipVelocity = ToDouble3(playerVelocity),
                SttTrackId = hasStt ? sttTrackId : default,
                SttProgress = designationProgress,
                Flags = flags,
            };
        }

        public bool TryGetBestTrack(out TrackSnapshot snapshot)
        {
            snapshot = default;
            return driver != null && driver.TryGetBestTrack(out snapshot);
        }

        public bool TryGetTrack(TrackId trackId, out TrackSnapshot snapshot)
        {
            snapshot = default;
            return driver != null && driver.TryGetTrack(trackId, out snapshot);
        }

        public bool TryReacquireTrack(
            in TrackSnapshot previousTrack,
            float basePositionGateMeters,
            out TrackSnapshot snapshot)
        {
            snapshot = default;
            if (!TryGetBestTrack(out var candidate))
            {
                return false;
            }

            var previousPosition = PredictTrackPosition(previousTrack);
            var candidatePosition = PredictTrackPosition(candidate);
            var previousAge = math.max(0.0, SimTimeSeconds - previousTrack.LastUpdateTime);
            var positionGate = math.max(
                basePositionGateMeters,
                basePositionGateMeters + math.length(previousTrack.Velocity) * previousAge * 0.5);
            if (math.distancesq(ToDouble3(previousPosition), ToDouble3(candidatePosition)) >
                positionGate * positionGate)
            {
                return false;
            }

            snapshot = candidate;
            return true;
        }

        /// <summary>
        /// Updates the player designation cursor. Only confirmed/coasting public tracks participate.
        /// </summary>
        public void UpdateDesignation(Camera viewCamera, Vector2 reticleViewport, float deltaTime)
        {
            if (!trackOperationMode || viewCamera == null)
            {
                return;
            }

            var count = CopyTracks(trackScratch);
            var bestId = default(TrackId);
            var bestScore = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var track = trackScratch[i];
                if (!IsFireControlUsable(track.Lifecycle))
                {
                    continue;
                }

                var position = PredictTrackPosition(track);
                var viewport = viewCamera.WorldToViewportPoint(position);
                if (viewport.z <= 0f)
                {
                    continue;
                }

                var ray = viewCamera.ViewportPointToRay(
                    new Vector3(reticleViewport.x, reticleViewport.y, 0f));
                if (Vector3.Angle(ray.direction, position - ray.origin) > DesignationGimbalDegrees)
                {
                    continue;
                }

                var dx = viewport.x - reticleViewport.x;
                var dy = viewport.y - reticleViewport.y;
                var screenDistance = Mathf.Sqrt(dx * dx + dy * dy);
                var relative = ToVector3(track.Velocity) - playerVelocity;
                var lineOfSight = (position - transform.position).normalized;
                var closingSpeed = Mathf.Max(0f, -Vector3.Dot(relative, lineOfSight));
                var threatBias = Mathf.Clamp01(closingSpeed / 500f) * 0.025f;
                var qualityBias = track.Confidence * 0.035f;
                var score = screenDistance - threatBias - qualityBias;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestId = track.TrackId;
                }
            }

            // Roughly the inner 12% of screen height is the designation capture area.
            if (!bestId.IsValid || bestScore > 0.12f)
            {
                candidateTrackId = default;
                designationProgress = Mathf.Max(0f, designationProgress - deltaTime * 2f);
                return;
            }

            if (!candidateTrackId.Equals(bestId))
            {
                candidateTrackId = bestId;
                designationProgress = 0f;
            }

            if (!TryGetTrack(candidateTrackId, out var candidate))
            {
                designationProgress = 0f;
                return;
            }

            var requiredSeconds = Mathf.Lerp(
                0.45f,
                0.22f,
                Mathf.Clamp01(candidate.Confidence * (1f - candidate.InterferenceScore)));
            designationProgress = Mathf.Clamp01(
                designationProgress + deltaTime / Mathf.Max(requiredSeconds, DesignationBaseSeconds * 0.5f));
            if (designationProgress < 1f)
            {
                return;
            }

            // A failed designation remains a TWS candidate. It is never deleted by the UI.
            if (candidate.Confidence >= 0.25f &&
                candidate.Lifecycle == TrackLifecycle.Confirmed)
            {
                sttTrackId = candidate.TrackId;
            }
        }

        public void CycleCandidate(int direction)
        {
            var count = CopyTracks(trackScratch);
            if (count == 0)
            {
                return;
            }

            var current = -1;
            for (var i = 0; i < count; i++)
            {
                if (trackScratch[i].TrackId.Equals(candidateTrackId))
                {
                    current = i;
                    break;
                }
            }

            for (var step = 1; step <= count; step++)
            {
                var index = (current + direction * step) % count;
                if (index < 0) index += count;
                if (IsFireControlUsable(trackScratch[index].Lifecycle))
                {
                    candidateTrackId = trackScratch[index].TrackId;
                    designationProgress = 0f;
                    return;
                }
            }
        }

        public bool TryGetFireSolution(
            float coneDegrees,
            double projectileSpeed,
            out TrackSnapshot track,
            out FireSolution solution)
        {
            solution = default;
            if (!TryGetFireControlTrack(out track))
            {
                return false;
            }

            solution = FireSolutionSolver.Solve(
                track,
                ToDouble3(transform.position),
                projectileSpeed,
                SimTimeSeconds);
            if (!solution.WeaponReleaseAuthorized)
            {
                return false;
            }

            var ray = GetAimRay();
            var toIntercept = ToVector3(solution.InterceptPoint) - ray.origin;
            return toIntercept.sqrMagnitude > 0.001f
                   && Vector3.Angle(ray.direction, toIntercept) <= coneDegrees;
        }

        public bool TryAuthorizeMissileLaunch(
            MissileGuidanceMode guidanceMode,
            float coneDegrees,
            double projectileSpeed,
            out TrackSnapshot track,
            out FireSolution solution,
            out int guidanceChannelToken)
        {
            guidanceChannelToken = 0;
            if (!TryGetFireSolution(coneDegrees, projectileSpeed, out track, out solution))
            {
                return false;
            }

            if (guidanceMode == MissileGuidanceMode.SemiActiveRadar && !track.TrackId.Equals(sttTrackId))
            {
                return false;
            }

            if (guidanceMode == MissileGuidanceMode.Infrared)
            {
                return solution.Quality >= 0.2f;
            }

            if (guidanceMode == MissileGuidanceMode.ActiveRadar ||
                guidanceMode == MissileGuidanceMode.Command)
            {
                return TryAcquireGuidanceChannel(track.TrackId, out guidanceChannelToken);
            }

            return true;
        }

        public void ReleaseGuidanceChannel(int token)
        {
            var index = token - 1;
            if ((uint)index >= guidanceChannels.Length ||
                guidanceChannels[index].ReferenceCount <= 0)
            {
                return;
            }

            guidanceChannels[index].ReferenceCount--;
            if (guidanceChannels[index].ReferenceCount == 0)
            {
                guidanceChannels[index] = default;
            }
        }

        public bool RetainGuidanceChannel(int token)
        {
            var index = token - 1;
            if ((uint)index >= guidanceChannels.Length ||
                guidanceChannels[index].ReferenceCount <= 0)
            {
                return false;
            }

            guidanceChannels[index].ReferenceCount++;
            return true;
        }

        public Vector3 PredictTrackPosition(in TrackSnapshot track)
        {
            var age = math.max(0.0, SimTimeSeconds - track.LastUpdateTime);
            return ToVector3(track.Position + track.Velocity * age);
        }

        private bool TryGetFireControlTrack(out TrackSnapshot track)
        {
            if (sttTrackId.IsValid && TryGetTrack(sttTrackId, out track) &&
                IsFireControlUsable(track.Lifecycle))
            {
                return true;
            }

            if (candidateTrackId.IsValid && TryGetTrack(candidateTrackId, out track) &&
                IsFireControlUsable(track.Lifecycle))
            {
                return true;
            }

            if (!TryGetBestTrack(out track))
            {
                return false;
            }

            return IsFireControlUsable(track.Lifecycle);
        }

        private static bool IsFireControlUsable(TrackLifecycle lifecycle) =>
            lifecycle == TrackLifecycle.Confirmed ||
            lifecycle == TrackLifecycle.Coasting ||
            lifecycle == TrackLifecycle.Degraded;

        private bool TryAcquireGuidanceChannel(TrackId trackId, out int token)
        {
            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount > 0 &&
                    guidanceChannels[i].TrackId.Equals(trackId))
                {
                    guidanceChannels[i].ReferenceCount++;
                    token = i + 1;
                    return true;
                }
            }

            for (var i = 0; i < guidanceChannels.Length; i++)
            {
                if (guidanceChannels[i].ReferenceCount != 0)
                {
                    continue;
                }

                guidanceChannels[i] = new GuidanceChannel
                {
                    TrackId = trackId,
                    ReferenceCount = 1,
                };
                token = i + 1;
                return true;
            }

            token = 0;
            return false;
        }

        private void Awake()
        {
            if (aimingCamera == null) aimingCamera = Camera.main;
        }

        private void FixedUpdate()
        {
            EnsureInitialized();
            if (!initialized)
            {
                return;
            }

            var dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            var playerPosition = transform.position;
            playerVelocity = (playerPosition - previousPlayerPosition) / dt;
            driver.SetEntityState(playerEntity, BuildState(transform, playerVelocity));
            previousPlayerPosition = playerPosition;

            for (var i = 0; i < sceneTargetCount; i++)
            {
                var sceneTarget = sceneTargets[i];
                var source = sceneTarget != null ? sceneTarget.AimPoint : null;
                var position = source != null ? source.position : previousTargetPositions[i];
                var velocity = (position - previousTargetPositions[i]) / dt;
                driver.SetEntityState(targetEntities[i], BuildState(source, velocity));
                previousTargetPositions[i] = position;
                UpdateTargetRegistration(i);
            }

            ValidateSttTrack();
            ScheduleDwells();
        }

        private void ScheduleDwells()
        {
            var now = driver.SimTimeSeconds;
            var aimRay = GetAimRay();
            var hasStt = HasStt;
            if (now + 1e-6 >= nextSearchDwellTime)
            {
                ScheduleSearchBatch(
                    now,
                    aimRay,
                    hasStt ? SttSearchDwellsPerTick : SearchDwellsPerTick);
                nextSearchDwellTime = now + SearchIntervalSeconds;
            }

            ScheduleTwsRevisits(now, hasStt);

            if (!hasStt || now + 1e-6 < nextSttDwellTime ||
                !TryGetTrack(sttTrackId, out var sttTrack))
            {
                return;
            }

            var sttPosition = PredictTrackPosition(sttTrack);
            var direction = (sttPosition - transform.position).normalized;
            if (direction.sqrMagnitude < 0.5f)
            {
                return;
            }

            driver.ScheduleRadarDwell(new Dwell
            {
                SensorId = sensorId,
                DueTime = now,
                DwellTimeSeconds = 0.028f,
                BeamDirection = direction,
                BeamWidthRadians = SttBeamWidthDegrees * Mathf.Deg2Rad,
                Priority = DwellPriority.SingleTargetTrack,
                Sequence = ++dwellSequence,
            });
            nextSttDwellTime = now + SttIntervalSeconds;
        }

        private void ScheduleSearchBatch(double now, Ray aimRay, int dwellCount)
        {
            for (var i = 0; i < dwellCount; i++)
            {
                if (searchPattern.IsFrameStart)
                {
                    CaptureSearchFrame(aimRay);
                }

                var cell = searchPattern.Next();
                var direction = SearchDirection(cell.x, cell.y);
                driver.ScheduleRadarDwell(new Dwell
                {
                    SensorId = sensorId,
                    DueTime = now,
                    DwellTimeSeconds = 0.008f,
                    BeamDirection = direction,
                    BeamWidthRadians = SearchBeamWidthDegrees * Mathf.Deg2Rad,
                    Priority = DwellPriority.WideSearch,
                    Sequence = ++dwellSequence,
                });
                UpdateBeamAngles(direction);
            }
        }

        private void ScheduleTwsRevisits(double now, bool hasStt)
        {
            var count = CopyTracks(trackScratch);
            var scheduled = 0;
            for (var i = 0; i < count && scheduled < TwsRevisitsPerTick; i++)
            {
                var track = trackScratch[i];
                if (track.Lifecycle == TrackLifecycle.Dropped ||
                    track.TrackId.Equals(sttTrackId))
                {
                    continue;
                }

                var slot = track.TrackId.Value.Index;
                if ((uint)slot >= nextTwsRevisitTimes.Length)
                {
                    continue;
                }

                if (twsRevisitTrackKeys[slot] != track.TrackId.Packed)
                {
                    twsRevisitTrackKeys[slot] = track.TrackId.Packed;
                    nextTwsRevisitTimes[slot] = now;
                }

                if (now + 1e-6 < nextTwsRevisitTimes[slot])
                {
                    continue;
                }

                var predictedPosition = PredictTrackPosition(track);
                var direction = (predictedPosition - transform.position).normalized;
                if (direction.sqrMagnitude < 0.5f)
                {
                    continue;
                }

                driver.ScheduleRadarDwell(new Dwell
                {
                    SensorId = sensorId,
                    DueTime = now,
                    DwellTimeSeconds = track.Lifecycle == TrackLifecycle.Tentative
                        ? 0.012f
                        : 0.016f,
                    BeamDirection = direction,
                    BeamWidthRadians = TwsBeamWidthDegrees * Mathf.Deg2Rad,
                    Priority = track.Lifecycle == TrackLifecycle.Tentative
                        ? DwellPriority.AlertConfirm
                        : DwellPriority.TrackWhileScan,
                    Sequence = ++dwellSequence,
                });
                nextTwsRevisitTimes[slot] = now +
                    (hasStt ? TwsRevisitIntervalSeconds * 1.5f : TwsRevisitIntervalSeconds);
                scheduled++;
            }
        }

        private void CaptureSearchFrame(Ray aimRay)
        {
            searchFrameForward = aimRay.direction.normalized;
            var referenceUp = aimingCamera != null
                ? aimingCamera.transform.up
                : transform.up;
            searchFrameRight = Vector3.Cross(referenceUp, searchFrameForward).normalized;
            if (searchFrameRight.sqrMagnitude < 0.5f)
            {
                searchFrameRight = transform.right;
            }

            searchFrameUp = Vector3.Cross(searchFrameForward, searchFrameRight).normalized;
        }

        private Vector3 SearchDirection(float azimuthDegrees, float elevationDegrees)
        {
            var horizontal = Mathf.Tan(azimuthDegrees * Mathf.Deg2Rad);
            var vertical = Mathf.Tan(elevationDegrees * Mathf.Deg2Rad);
            return (
                searchFrameForward +
                searchFrameRight * horizontal +
                searchFrameUp * vertical).normalized;
        }

        private float GetSearchFrameRate(bool hasStt)
        {
            var dwellsPerTick = hasStt
                ? SttSearchDwellsPerTick
                : SearchDwellsPerTick;
            var frameSeconds =
                searchPattern.CellCount * SearchIntervalSeconds / dwellsPerTick;
            return frameSeconds > 0f ? 1f / frameSeconds : 0f;
        }

        private void ValidateSttTrack()
        {
            if (!sttTrackId.IsValid)
            {
                return;
            }

            if (!TryGetTrack(sttTrackId, out var track) ||
                track.Lifecycle == TrackLifecycle.Dropped ||
                SimTimeSeconds - track.LastUpdateTime > 3.0)
            {
                BreakSingleTargetTrack();
            }
        }

        private void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            if (aimingCamera == null) aimingCamera = Camera.main;
            driver = FindAnyObjectByType<SimulationDriver>();
            if (driver == null)
            {
                driver = new GameObject("AC6_Radar_Simulation").AddComponent<SimulationDriver>();
            }

            previousPlayerPosition = transform.position;
            playerEntity = driver.RegisterEntity(BuildState(transform, Vector3.zero));
            DiscoverTargets();
            driver.RegisterRadarSensor(new RadarSensorProfile
            {
                SensorId = sensorId,
                EmitterId = new EmitterId(new Handle(0, 1)),
                PlatformId = new PlatformId(new Handle(0, 1)),
                OwnerEntityId = playerEntity,
                DwellConfig = new RadarDwellConfig
                {
                    ConstantsDb = 215f,
                    IntegrationGainDb = 6f,
                    NoisePowerDb = 0f,
                    DetectThresholdLowDb = 3f,
                    DetectThresholdHighDb = 10f,
                    RangeResolutionMeters = 12f,
                    BeamWidthRadians = SearchBeamWidthDegrees * Mathf.Deg2Rad,
                    VelocityResolutionMps = 2f,
                },
                Capability = SensorCapability.Range | SensorCapability.Doppler | SensorCapability.LosRate,
                MaxRangeMeters = RadarRangeMeters,
                FrequencyBand = 1,
                WaveformId = 1,
                MainlobePowerDb = 70f,
                SidelobeLevelDb = -25f,
                ClutterPowerDb = -30f,
                Resolutions = new ResolutionMerge.Resolutions
                {
                    RangeMeters = 12f,
                    AngleRadians = 1.5f * Mathf.Deg2Rad,
                    VelocityMps = 2f,
                },
            });
            initialized = true;

            for (var i = 0; i < sceneTargetCount; i++)
            {
                UpdateTargetRegistration(i);
            }
        }

        private void DiscoverTargets()
        {
            var discovered = FindObjectsByType<CombatTestTarget>();
            if (configuredTarget != null)
            {
                AddSceneTarget(configuredTarget);
            }

            for (var i = 0; i < discovered.Length && sceneTargetCount < MaximumSceneTargets; i++)
            {
                AddSceneTarget(discovered[i]);
            }
        }

        private void AddSceneTarget(CombatTestTarget sceneTarget)
        {
            if (sceneTarget == null || sceneTargetCount >= MaximumSceneTargets)
            {
                return;
            }

            for (var i = 0; i < sceneTargetCount; i++)
            {
                if (sceneTargets[i] == sceneTarget)
                {
                    return;
                }
            }

            var source = sceneTarget.AimPoint;
            sceneTargets[sceneTargetCount] = sceneTarget;
            previousTargetPositions[sceneTargetCount] =
                source != null ? source.position : sceneTarget.transform.position;
            targetEntities[sceneTargetCount] =
                driver.RegisterEntity(BuildState(source, Vector3.zero));
            sceneTargetCount++;
        }

        private void UpdateTargetRegistration(int index)
        {
            var sceneTarget = sceneTargets[index];
            var shouldRegister = sceneTarget != null && sceneTarget.IsAlive;
            if (shouldRegister == targetRadarRegistered[index])
            {
                return;
            }

            if (shouldRegister)
            {
                driver.RegisterRadarTarget(new RadarTargetProfile
                {
                    EntityId = targetEntities[index],
                    RadarCrossSectionSquareMeters = sceneTarget.RadarCrossSectionSquareMeters,
                });
            }
            else
            {
                driver.UnregisterRadarTarget(targetEntities[index]);
            }

            targetRadarRegistered[index] = shouldRegister;
        }

        private void UpdateBeamAngles(Vector3 worldDirection)
        {
            var local = transform.InverseTransformDirection(worldDirection.normalized);
            beamAzimuthDegrees = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            beamElevationDegrees = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private Ray GetAimRay()
        {
            if (aimingCamera == null) aimingCamera = Camera.main;
            return aimingCamera != null
                ? aimingCamera.ViewportPointToRay(new Vector3(0.5f, 0.56f, 0f))
                : new Ray(transform.position, transform.forward);
        }

        private static KinematicState BuildState(Transform source, Vector3 velocity)
        {
            return new KinematicState
            {
                PositionWorld = source != null ? ToDouble3(source.position) : double3.zero,
                VelocityWorld = ToDouble3(velocity),
                Rotation = source != null ? source.rotation : quaternion.identity,
                AngularVelocity = float3.zero,
                Mass = 1.0,
            };
        }

        private static double3 ToDouble3(Vector3 value) =>
            new double3(value.x, value.y, value.z);

        private static Vector3 ToVector3(double3 value) =>
            new Vector3((float)value.x, (float)value.y, (float)value.z);

        private void OnDestroy()
        {
            if (!initialized || driver == null)
            {
                return;
            }

            for (var i = 0; i < sceneTargetCount; i++)
            {
                driver.UnregisterRadarTarget(targetEntities[i]);
                driver.UnregisterEntity(targetEntities[i]);
            }

            driver.UnregisterEntity(playerEntity);
        }
    }
}
