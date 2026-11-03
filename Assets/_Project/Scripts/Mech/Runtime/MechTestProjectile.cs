using System;
using System.Collections.Generic;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Driver;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
	public enum InterceptorEngagementOutcome : byte
	{
		Missed = 0,
		Damaged = 1,
		Destroyed = 2,
	}

	public readonly struct InterceptorEngagementReport
	{
		public InterceptorEngagementReport(
			Transform owner,
			TrackId targetTrackId,
			InterceptorEngagementOutcome outcome,
			string reason)
		{
			Owner = owner;
			TargetTrackId = targetTrackId;
			Outcome = outcome;
			Reason = reason;
		}

		public Transform Owner { get; }
		public TrackId TargetTrackId { get; }
		public InterceptorEngagementOutcome Outcome { get; }
		public string Reason { get; }
	}

	[DisallowMultipleComponent]
	public sealed class MechTestProjectile : MonoBehaviour
	{
        private System.Guid impactEventId = System.Guid.NewGuid();
        public string SourceSlotId { get; private set; }
        public System.Guid SourceEquipmentId { get; private set; }
        public System.Guid LaunchIdentity { get; private set; }
        public long SourceCommandSequence { get; private set; }
        private bool externalGuidanceSuppressed;
        public bool HasExternalGuidanceSupport => !externalGuidanceSuppressed && !guidanceChannelReleased && guidanceChannelToken > 0;
        public bool StopExternalGuidanceSupport()
        {
            if (externalGuidanceSuppressed) return false;
            externalGuidanceSuppressed = true;
            ReleaseGuidanceChannel();
            return true;
        }
        public void SetLaunchIdentity(string slotId, System.Guid equipmentId, long sequence)
        { SourceSlotId = slotId; SourceEquipmentId = equipmentId; SourceCommandSequence = sequence; }

		private static readonly List<MechTestProjectile> registry = new List<MechTestProjectile>();

		public static event Action<InterceptorEngagementReport> InterceptorEngagementResolved;

		private const float GuidanceMemorySeconds = 4f;

		private const float InterceptorGuidanceMemorySeconds = 0.8f;

		private const float ReacquisitionGateMeters = 350f;

		private const float InterceptorReacquisitionGateMeters = 100f;

		private const float InterceptorAcquisitionGateMinimumMeters = 80f;

		private const float InterceptorAcquisitionGateMaximumMeters = 300f;

		private const float InterceptorVelocityAssociationGateMps = 300f;

		private const float InterceptorSeekerPositionBlend = 0.35f;

		private const float InterceptorSeekerVelocityBlend = 0.2f;

		private const float SeekerScanIntervalSeconds = 0.05f;

		private const float SeekerLockMemorySeconds = 0.35f;

		private const float MinimumSeekerActivationDelaySeconds = 0.5f;

		private const float InterceptorSeekerLockMemorySeconds = 0.65f;

		private const float InterceptorFuseMeasurementMaximumAgeSeconds = 0.12f;

		private const float InterceptorFuseEstimateMemorySeconds = 4f;

		private const float InterceptorFuseLookAheadSeconds = 0.06f;

		private const float InterceptorFuseUncertaintyAllowanceMeters = 8f;

		private const float DefaultMissileInterceptionHealth = 80f;

		private const float MissileHitboxHalfWidthMeters = 0.25f;

		private const float AirToAirMidcourseLeadLimitSeconds = 15f;

		private const float AirToAirTerminalLeadLimitSeconds = 4f;

		private const float InterceptorMidcourseLeadLimitSeconds = 4f;

		private const float InterceptorTerminalLeadLimitSeconds = 1.5f;

		private const float EngagementAssessmentIntervalSeconds = 0.2f;

		private const float EngagementFailureConfirmationSeconds = 0.75f;

		private const float GuidanceRecoveryGraceSeconds = 2f;

		private const float MinimumEngagementAssessmentFlightSeconds = 1f;

		private const float MinimumOpeningSpeedMetersPerSecond = 5f;

		private static readonly RaycastHit[] SphereCastScratch = new RaycastHit[32];

		private static readonly Collider[] OverlapScratch = new Collider[64];

		private static readonly Dictionary<CombatTestTarget, BlastContact> BlastContactScratch = new Dictionary<CombatTestTarget, BlastContact>();

		private static readonly HashSet<MechTestProjectile> DamagedProjectileScratch = new HashSet<MechTestProjectile>();

		private struct BlastContact
		{
			public Vector3 Point;

			public float Distance;
		}

		private Transform owner;

		private MechRadarController radarController;

		private MechRadarController dataLinkController;

		private bool interceptorMissile;

		private bool configured;

		private bool interceptorOutcomeReported;

		private bool interceptorTargetCommitted;

		private TrackId targetTrackId;

		private TestWeaponKind weaponKind;

		private Vector3 velocity;

		private float damage;

		private float collisionRadius;

		private float lifeRemaining;

		private float launchDelay;

		private float maximumTravelDistance;

		private float distanceTravelled;

		private float projectileFlightTime;
        private double missileLaunchTimestamp;

		private MechEffectPrefabLibrary effectLibrary;

		private int impactFfxId;

		private TrackSnapshot cachedGuidanceTrack;

		private bool hasCachedGuidanceTrack;

		private float guidanceMemoryRemaining;

		private int guidanceChannelToken;

		private bool guidanceChannelReleased;

		private MissileFlightPhase missilePhase;

		private float missileFlightTime;

		private float dryMassKg;

		private float mainPropellantMassRemainingKg;

		private float mainEngineThrustNewtons;

		private float mainMassFlowKgPerSecond;

		private MissilePropulsionMode propulsionMode;

		private float secondStagePropellantMassRemainingKg;

		private float secondStageThrustNewtons;

		private float secondStageMassFlowKgPerSecond;

		private float secondStageIgnitionDelaySeconds;

		private float secondStageMinimumThrottle;
        private float nextSustainThrottleReview;
        private float sustainBudgetThrottle;

		private float firstStageBurnoutTime = -1f;

		private bool secondStageIgnited;
        private bool adaptiveSecondPulse;
        private float secondPulseDemandSeconds;
        private Vector3 pulseObservedPosition,pulseObservedVelocity;
        private double pulseObservationTime=double.NegativeInfinity;
        public string SecondPulseIgnitionReason {get;private set;}
        public float SecondPulseIgnitionTime {get;private set;} = -1f;

		private float lastGuidanceRangeMeters = float.PositiveInfinity;

		private float rcsPropellantMassRemainingKg;

		private float rcsThrustNewtons;

		private float rcsMassFlowKgPerSecond;

		private float pitchYawInertiaKgM2;

		private float rcsMomentArmMeters;

		private float maximumAngularRateRadians;

		private float currentAngularRateRadians;

		private float seekerActivationDelay;

		private bool seekerActivated;

		private float seekerMaximumRange;

		private float seekerHalfAngleRadians;

		private float seekerGimbalRadians;

		private Vector3 rcsAccelerationWorld;

		private Vector3 rcsAttitudeTorqueLocal;

		private float rcsAttitudeThrottle;

		private float proximityFuseRadius;

		private float proximityFuseArmTime;

		private float proximityFuseSafeDistance;

		private float warheadLethalRadius;

		private float seekerScanRemaining;

		private float seekerLockMemoryRemaining;

		private bool seekerLocked;

		private Vector3 desiredAttitudeForward;

		private Vector3 seekerEstimatedPosition;

		private Vector3 seekerEstimatedVelocity;

		private Vector3 seekerFuseEstimatedPosition;

		private Vector3 seekerFuseEstimatedVelocity;

		private Vector3 terminalFuseEstimatedPosition;

		private Vector3 terminalFuseEstimatedVelocity;

		private float terminalFuseEstimateMemoryRemaining;

		private float terminalFuseEstimateUncertaintyMeters;

		private Vector3 prelaunchInterceptPoint;

		private float prelaunchSteeringRemaining;

		private bool coldLaunchEnabled;

		private bool coldLaunchMotorIgnited;

		private Vector3 coldLaunchAxis;

		private Vector3 coldLaunchInheritedVelocity;

		private Vector3 coldLaunchPreviousPosition;

		private Vector3 coldLaunchCarrierPreviousPosition;

		private float coldLaunchElapsedSeconds;

		private float coldLaunchRelativeDistanceMeters;

		private float coldLaunchSafeSeparationSeconds;

		private float coldLaunchSafeSeparationMeters;

		private float coldLaunchIgnitionAlignmentRadians;

		private float coldLaunchAlignmentTimeoutRemaining;

		private double seekerLastMeasurementTime = -1.0;
        private double guidanceTimeOffset;
        private double guidanceMemoryExpiresAt = double.NegativeInfinity;
        private int? launchTeam;
        private float observerRefreshRemaining;
        internal int? LaunchTeam => launchTeam;
        private MechMissileSeekerRuntime seekerRuntime;
        private bool passiveRf;
        public bool PassiveRfHomingEnabled=>passiveRf;
        public bool ActiveSeekerEnabled=>seekerActivated;
        private ushort passiveBand,passiveWaveform;
        private double passiveLastSeen=double.NegativeInfinity,passiveNextListen;
        private float passiveMemorySeconds=4f;
        private Vector3 passiveBearing;
        public void EnablePassiveRfHoming()
        {
            if(dataLinkController!=null&&hasCachedGuidanceTrack&&dataLinkController.TryAssociatePassiveTarget(cachedGuidanceTrack,out var source))
            {passiveRf=true;passiveBand=source.FrequencyBand;passiveWaveform=source.WaveformId;passiveLastSeen=GuidanceTime;
                guidanceMemoryExpiresAt=cachedGuidanceTrack.LastUpdateTime+GetGuidanceMemorySeconds();
                StopExternalGuidanceSupport();if(linkTerminal.IsValid)dataLinkController.EndGuidanceUplink(linkTerminal);}
        }
        private double GuidanceTime => guidanceTimeOffset + projectileFlightTime;
        private Vector3 PredictRememberedTrack(in TrackSnapshot track) => ToVector3((float3)track.PredictPosition(GuidanceTime));

		private ulong seekerDwellSequence;

		private MechRadarController.MissileSeekerHandle seekerHandle;

		private readonly List<RadarObserverRegistration> radarObservers =
			new List<RadarObserverRegistration>();

		private readonly Measurement[] seekerMeasurementScratch = new Measurement[8];

		private LinkTerminalId linkTerminal;

		private float friendlyLinkReportRemaining;
        private float friendlyLinkRetryRemaining;

		private ulong lastLinkSequence;

		private readonly LinkMessage[] linkInboxScratch = new LinkMessage[8];

		private bool irSourceRegistered;

		private readonly MechProjectileHeat thermalState = new MechProjectileHeat();
        private MechThermalPointEmitter thermalEmitter;
        public float ThermalTemperatureK => thermalState.TemperatureK;
        public float InfraredIntensityDb => thermalState.IntensityDb;

		private const float MotorIrIntensityDb = 58f;

		private const float CoastIrIntensityDb = 28f;

		private ParticleSystem[] propulsionParticles = Array.Empty<ParticleSystem>();

		private TrailRenderer projectileTrail;

		private bool motorVisualActive;

		private MechMissileAudioController missileAudio;

		private ParticleSystem[] rcsParticles = Array.Empty<ParticleSystem>();

		private Vector3[] rcsExhaustDirectionsLocal = Array.Empty<Vector3>();

		private Vector3[] rcsTorqueAxesLocal = Array.Empty<Vector3>();

		private float[] rcsPulsePhase = Array.Empty<float>();

		private MechMissilePlume missilePlume;

		private Transform missilePresentationRoot;

		private MechMissileVisualInterpolator missileVisualInterpolator;

		private const float MissilePlumeFallbackNozzleOffset = 1.8f;

		private const float MissilePlumeScale = 1f;

		public void ConfigureMissilePresentation(Transform presentationRoot)
		{
			missilePresentationRoot = presentationRoot;
			if (missilePresentationRoot == null)
			{
				return;
			}

			missileVisualInterpolator = missilePresentationRoot.GetComponent<MechMissileVisualInterpolator>();
			if (missileVisualInterpolator == null)
			{
				missileVisualInterpolator = missilePresentationRoot.gameObject.AddComponent<MechMissileVisualInterpolator>();
			}
			missileVisualInterpolator.Configure(base.transform);
		}

		private static Material rcsParticleMaterial;

		private const float RcsPulseHertz = 14f;

		private const float RcsPulsePeakRate = 260f;

		private float interceptionHealth;

		private float interceptorRoundLethalRadius;

		private float interceptorRoundArmSeconds;

		private float interceptorRoundSafeDistanceMeters;

		private bool cannonProximityFuseEnabled;

		private bool interceptionResolved;

		private MissileEngagementState engagementState;

		private float engagementAssessmentRemaining;

		private float engagementFailureEvidenceSeconds;

		private float noGuidanceSeconds;

		private bool hasObservedClosingApproach;

		private bool missedTargetDetonationPending;

		private float missedTargetDetonationTime;

		private Vector3 missedTargetDetonationPoint;

		private string missedTargetDetonationReason;

		public static IReadOnlyList<MechTestProjectile> Registry => registry;

		public MissileFlightPhase MissilePhase => missilePhase;

		public MissileEngagementState EngagementState => engagementState;

		public bool IsEngagementActive
		{
			get
			{
				if (IsMissile)
				{
					return engagementState == MissileEngagementState.Active;
				}
				return false;
			}
		}

		public bool SeekerLocked => seekerLocked;

		public float CurrentMassKg => dryMassKg + mainPropellantMassRemainingKg + secondStagePropellantMassRemainingKg + rcsPropellantMassRemainingKg;

		public float MainPropellantMassRemainingKg => mainPropellantMassRemainingKg;

		public float RcsPropellantMassRemainingKg => rcsPropellantMassRemainingKg;

		public float ManeuverDeltaVRemaining => EstimateRcsDeltaVRemaining();

        private double birthStateTime,physicsStateTime,previousPhysicsTime;private Vector3 previousPhysicsPosition;
        public Vector3 PositionAtPhysicsTime(double time)
        {
            time=Math.Max(time,birthStateTime);
            if(physicsStateTime>previousPhysicsTime&&time>=previousPhysicsTime&&time<=physicsStateTime)
                return Vector3.Lerp(previousPhysicsPosition,transform.position,(float)((time-previousPhysicsTime)/(physicsStateTime-previousPhysicsTime)));
            return transform.position+velocity*(float)Math.Max(-.1,Math.Min(.1,time-physicsStateTime));
        }
		public float PhysicalCollisionRadius => collisionRadius;
		public Vector3 Velocity => velocity;
        public Vector3 PresentationPosition => missilePresentationRoot!=null?missilePresentationRoot.position:transform.position;
        public bool TryGetFriendlyReportTerminal(MechRadarController observer,out LinkTerminalId terminal)
        {
            terminal=default;
            if(dataLinkController==observer&&linkTerminal.IsValid){terminal=linkTerminal;return true;}
            foreach(var registration in radarObservers)if(registration.Controller==observer&&registration.FriendlyReportTerminal.IsValid){terminal=registration.FriendlyReportTerminal;return true;}
            return false;
        }

        public double PhysicsStateTime => physicsStateTime;

		public bool IsMissile => weaponKind == TestWeaponKind.Missile;

		public bool IsInterceptorMissile => IsMissile && interceptorMissile;

		public bool IsInterceptableMissile
		{
			get
			{
				if (IsMissile && !interceptionResolved)
				{
					return base.gameObject.activeInHierarchy;
				}
				return false;
			}
		}

		public bool HasActiveGuidance
		{
			get
			{
				if (!IsEngagementActive)
				{
					return false;
				}

				return seekerLocked ||
				       (hasCachedGuidanceTrack && guidanceMemoryRemaining > 0f);
			}
		}

		public TrackId GuidanceTrackId
		{
			get
			{
				if (!hasCachedGuidanceTrack || !cachedGuidanceTrack.TrackId.IsValid)
				{
					return targetTrackId;
				}
				return cachedGuidanceTrack.TrackId;
			}
		}

		public bool IsOwnedBy(Transform candidateOwner)
		{
			if (candidateOwner != null)
			{
				return owner == candidateOwner;
			}
			return false;
		}

		// GetComponentInParent from the owner, memoized while the owner and its parent are
		// unchanged (re-resolved if the found body is destroyed).
		private Transform ownerTargetSource, ownerTargetParent;
		private CombatTestTarget ownerTarget;
		private CombatTestTarget OwnerTarget()
		{
			var parent = owner != null ? owner.parent : null;
			if (owner != ownerTargetSource || parent != ownerTargetParent || (ownerTarget == null && !ReferenceEquals(ownerTarget, null)))
			{
				ownerTargetSource = owner; ownerTargetParent = parent;
				ownerTarget = owner != null ? owner.GetComponentInParent<CombatTestTarget>() : null;
			}
			return ownerTarget;
		}

		private bool SharesTeamWith(MechTestProjectile candidate)
		{
			if (candidate == null || owner == null || candidate.owner == null)
			{
				return false;
			}
			CombatTestTarget ownerTarget = OwnerTarget();
			CombatTestTarget candidateOwnerTarget = candidate.OwnerTarget();
			return ownerTarget != null && candidateOwnerTarget != null && ownerTarget.TeamId == candidateOwnerTarget.TeamId;
		}

		private void OnEnable()
		{
			if (!registry.Contains(this))
			{
				registry.Add(this);
			}
		}

		private void OnDisable()
		{
			if (configured && IsInterceptorMissile && !interceptorOutcomeReported)
			{
				ReportInterceptorOutcome(
					InterceptorEngagementOutcome.Missed,
					"projectile_removed");
			}
            ReleaseGuidanceChannel();
            ReleaseRadarRegistrations();
            configured = false;
			registry.Remove(this);
		}

		private Unity.Collections.FixedString128Bytes knownMissileModel;
        public void Configure(Transform newOwner, MechRadarController newRadarController, TrackId newTargetTrackId, TestWeaponKind kind, Vector3 initialVelocity, float newDamage, float newCollisionRadius, float lifetimeSeconds, float delaySeconds = 0f, float accelerationMetersPerSecondSquared = 0f, float turnRateDegreesPerSecond = 0f, MechEffectPrefabLibrary newEffectLibrary = null, int newImpactFfxId = 0, float newMaximumTravelDistance = 0f, int newGuidanceChannelToken = 0, float newMotorBurnDuration = 2.1f, float newSeekerActivationDelay = 2.5f, float newSeekerMaximumRange = 8000f, float newSeekerHalfAngleDegrees = 35f, float newSeekerGimbalDegrees = 60f, float newMaximumLateralAcceleration = 75f, float newManeuverDeltaV = 180f, float newRcsLateralAcceleration = 0f, float newRcsDeltaV = 0f, float newProximityFuseRadius = 12f, float newProximityFuseArmTime = 0.75f, float newProximityFuseSafeDistance = 20f, float newWarheadLethalRadius = 18f, bool newInterceptorMissile = false, MissileFlightProfile missileProfile = default(MissileFlightProfile), bool hasInitialGuidanceEstimate = false, Vector3 initialGuidancePosition = default(Vector3), Vector3 initialGuidanceVelocity = default(Vector3), float initialGuidanceUncertaintyMeters = 0f, Vector3 newPrelaunchInterceptPoint = default(Vector3), bool newColdLaunch = false, Vector3 newColdLaunchInheritedVelocity = default(Vector3), float newColdLaunchSafeSeparationSeconds = 0f, float newColdLaunchSafeSeparationMeters = 0f, float newColdLaunchIgnitionAlignmentDegrees = 8f, float newColdLaunchAlignmentTimeoutSeconds = 0.6f, bool newOwnshipMissileAudio = false,string newMissileModel=null)
		{
			owner = newOwner;knownMissileModel=new Unity.Collections.FixedString128Bytes(string.IsNullOrWhiteSpace(newMissileModel)?"unknown":newMissileModel.Substring(0,Math.Min(30,newMissileModel.Length)));
            var launchPlatform = newOwner!=null?newOwner.GetComponentInParent<CombatTestTarget>():null;
            launchTeam = launchPlatform!=null?(int?)launchPlatform.TeamId:null;
			dataLinkController = newOwner != null
				? newOwner.GetComponent<MechRadarController>() ??
				  newOwner.GetComponentInParent<MechRadarController>()
				: null;
			radarController = dataLinkController ?? newRadarController;
			if (dataLinkController == null &&
				newRadarController != null &&
				newOwner != null &&
				ResolveCombatantRoot(newOwner) ==
				ResolveCombatantRoot(newRadarController.transform))
			{
				dataLinkController = newRadarController;
			}
			interceptorMissile = newInterceptorMissile;
			interceptorOutcomeReported = false;
			interceptorTargetCommitted = newInterceptorMissile &&
				(newTargetTrackId.IsValid || hasInitialGuidanceEstimate);
			targetTrackId = newTargetTrackId;
			weaponKind = kind;
			engagementState = MissileEngagementState.Active;
			engagementAssessmentRemaining = 0f;
			engagementFailureEvidenceSeconds = 0f;
			noGuidanceSeconds = 0f;
			hasObservedClosingApproach = false;
			missedTargetDetonationPending = false;
			missedTargetDetonationTime = float.PositiveInfinity;
			missedTargetDetonationPoint = default(Vector3);
			missedTargetDetonationReason = string.Empty;
			interceptionHealth = (IsMissile ? 80f : 0f);
			interceptionResolved = false;
			velocity = initialVelocity;
            birthStateTime=physicsStateTime=previousPhysicsTime=Time.timeAsDouble;previousPhysicsPosition=transform.position;
			desiredAttitudeForward = ((base.transform.forward.sqrMagnitude > 0.001f) ? base.transform.forward.normalized : ((initialVelocity.sqrMagnitude > 0.001f) ? initialVelocity.normalized : Vector3.forward));
			if (hasInitialGuidanceEstimate)
			{
				terminalFuseEstimatedPosition = initialGuidancePosition;
				terminalFuseEstimatedVelocity = initialGuidanceVelocity;
				terminalFuseEstimateMemoryRemaining = GetTerminalEstimateMemorySeconds();
				terminalFuseEstimateUncertaintyMeters = Mathf.Max(0f, initialGuidanceUncertaintyMeters);
			}
			prelaunchInterceptPoint = newPrelaunchInterceptPoint;
			prelaunchSteeringRemaining = ((newInterceptorMissile && (prelaunchInterceptPoint - base.transform.position).sqrMagnitude > 0.001f) ? 0.6f : 0f);
			coldLaunchEnabled = weaponKind == TestWeaponKind.Missile && newColdLaunch;
			coldLaunchMotorIgnited = !coldLaunchEnabled;
			coldLaunchAxis = ((base.transform.forward.sqrMagnitude > 0.001f) ? base.transform.forward.normalized : ((initialVelocity.sqrMagnitude > 0.001f) ? initialVelocity.normalized : Vector3.forward));
			coldLaunchInheritedVelocity = newColdLaunchInheritedVelocity;
			coldLaunchPreviousPosition = base.transform.position;
			coldLaunchCarrierPreviousPosition = ((owner != null) ? owner.position : Vector3.zero);
			coldLaunchElapsedSeconds = 0f;
			coldLaunchRelativeDistanceMeters = 0f;
			coldLaunchSafeSeparationSeconds = Mathf.Max(0f, newColdLaunchSafeSeparationSeconds) + (newInterceptorMissile ? 0f : 0.25f);
			coldLaunchSafeSeparationMeters = Mathf.Max(0f, newColdLaunchSafeSeparationMeters);
			coldLaunchIgnitionAlignmentRadians = Mathf.Clamp(newColdLaunchIgnitionAlignmentDegrees, 0.5f, 45f) * (MathF.PI / 180f);
			coldLaunchAlignmentTimeoutRemaining = Mathf.Max(0.05f, newColdLaunchAlignmentTimeoutSeconds);
			damage = newDamage;
			collisionRadius = Mathf.Max(0.01f, newCollisionRadius);
			lifeRemaining = Mathf.Max(0.1f, lifetimeSeconds);
			launchDelay = Mathf.Max(0f, delaySeconds);
			maximumTravelDistance = Mathf.Max(0f, newMaximumTravelDistance);
			distanceTravelled = 0f;
			projectileFlightTime = 0f;
            missileLaunchTimestamp = birthStateTime;
            guidanceTimeOffset = radarController != null ? radarController.SimTimeSeconds : 0.0;
			cannonProximityFuseEnabled = false;
			effectLibrary = newEffectLibrary;
			impactFfxId = newImpactFfxId;
			impactEventId = System.Guid.NewGuid();
            externalGuidanceSuppressed = false;
            guidanceChannelToken = newGuidanceChannelToken;
			guidanceChannelReleased = guidanceChannelToken <= 0;
			if (weaponKind == TestWeaponKind.Missile)
			{
				ConfigureMissileDynamics(missileProfile, accelerationMetersPerSecondSquared, turnRateDegreesPerSecond, newMotorBurnDuration, newSeekerActivationDelay, newSeekerMaximumRange, newSeekerHalfAngleDegrees, newSeekerGimbalDegrees, newMaximumLateralAcceleration, newManeuverDeltaV, newRcsLateralAcceleration, newRcsDeltaV, newProximityFuseRadius, newProximityFuseArmTime, newProximityFuseSafeDistance, newWarheadLethalRadius);
			}
			hasCachedGuidanceTrack = weaponKind == TestWeaponKind.Missile && targetTrackId.IsValid && dataLinkController != null && dataLinkController.TryGetTrack(targetTrackId, out cachedGuidanceTrack);
			guidanceMemoryRemaining = (hasCachedGuidanceTrack ? GetGuidanceMemorySeconds() : 0f);
            guidanceMemoryExpiresAt = hasCachedGuidanceTrack?cachedGuidanceTrack.LastUpdateTime+GetGuidanceMemorySeconds():GuidanceTime;
			projectileTrail = GetComponent<TrailRenderer>();
			propulsionParticles = GetComponentsInChildren<ParticleSystem>(includeInactive: true);
			if (weaponKind == TestWeaponKind.Missile)
			{
				missilePlume = base.gameObject.AddComponent<MechMissilePlume>();
				missilePlume.Build(1.8f, 1f, missilePresentationRoot);
				CreateRcsThrusterVisuals(missilePresentationRoot);
			}
			if (weaponKind == TestWeaponKind.Missile)
			{
                seekerRuntime?.Dispose();
                seekerRuntime = new MechMissileSeekerRuntime(this, interceptorMissile, seekerMaximumRange, seekerHalfAngleRadians * Mathf.Rad2Deg * 2f);
				RegisterRadarObservers();
			}
			if (weaponKind == TestWeaponKind.Missile &&
				dataLinkController != null &&
				dataLinkController.TryRegisterMissileLinkTerminal(
					base.transform.position,
					velocity,
					out linkTerminal,
                    source: BattlefieldEntityIdentity.GetOrAdd(gameObject)))
			{
                dataLinkController.RecordOwnMissileLaunch(linkTerminal,transform.position,velocity,interceptorMissile?MissileMissionRole.Interceptor:MissileMissionRole.AirToAir,knownMissileModel,birthStateTime);
				friendlyLinkReportRemaining = 0f;
				if (targetTrackId.IsValid)
				{
					if (!dataLinkController.BeginGuidanceUplink(
						linkTerminal,
						targetTrackId,
						!interceptorMissile))
                        Debug.LogWarning("[GuidanceUplink] "+dataLinkController.LastGuidanceUplinkRejection+"；导弹依靠记忆与自身导引头飞行。",this);
				}
			}
			else if (weaponKind == TestWeaponKind.Missile &&
				dataLinkController != null)
			{
				Debug.LogWarning(
					"[MissileIFF] Launcher data-link terminal allocation failed; " +
					"this missile cannot transmit authenticated identity reports.",
					this);
			}
			missilePhase = (coldLaunchEnabled ? MissileFlightPhase.ColdEjection : ((!(launchDelay > 0f)) ? MissileFlightPhase.Boost : MissileFlightPhase.LaunchDelay));
			if (weaponKind == TestWeaponKind.Missile)
			{
				missileAudio = GetComponent<MechMissileAudioController>();
				if (missileAudio == null)
				{
					missileAudio = base.gameObject.AddComponent<MechMissileAudioController>();
				}
				missileAudio.Configure(coldLaunchEnabled, newOwnshipMissileAudio);
				bool motorStartsImmediately = !coldLaunchEnabled && launchDelay <= 0f;
                thermalEmitter=GetComponent<MechThermalPointEmitter>();
                if(thermalEmitter==null)thermalEmitter=gameObject.AddComponent<MechThermalPointEmitter>();
                thermalState.Step(motorStartsImmediately,0);
                thermalEmitter.SetTemperature(thermalState.TemperatureK);
				motorVisualActive = !motorStartsImmediately;
				SetMotorVisual(motorStartsImmediately);
			}
            if(weaponKind==TestWeaponKind.Missile){LaunchIdentity=System.Guid.NewGuid();dataLinkController?.RecordOwnedMissileLaunch(LaunchIdentity,transform.position,velocity,interceptorMissile?MissileMissionRole.Interceptor:MissileMissionRole.AirToAir,knownMissileModel,birthStateTime);dataLinkController?.SetOwnedMissileAssignedTarget(LaunchIdentity,GuidanceTrackId);dataLinkController?.BindOwnedMissileTerminal(LaunchIdentity,linkTerminal);}
			configured = true;
            MechCombatObservationSensor.Launched(this);
			if (weaponKind == TestWeaponKind.Missile)
			{
				RecordProjectileTelemetry(
					"projectile_launched",
					string.Empty,
					string.Empty);
			}
		}

		private void ConfigureMissileDynamics(MissileFlightProfile profile, float legacyAcceleration, float legacyTurnRateDegrees, float legacyMotorBurnDuration, float legacySeekerActivationDelay, float legacySeekerMaximumRange, float legacySeekerHalfAngleDegrees, float legacySeekerGimbalDegrees, float legacyMaximumLateralAcceleration, float legacyManeuverDeltaV, float legacyRcsLateralAcceleration, float legacyRcsDeltaV, float legacyProximityFuseRadius, float legacyProximityFuseArmTime, float legacyProximityFuseSafeDistance, float legacyWarheadLethalRadius)
		{
			if (!profile.IsConfigured)
			{
				profile = MissileFlightProfile.AirToAirDefault;
				float initialMass = profile.InitialMassKg;
				if (legacyAcceleration > 0f)
				{
					profile.MainEngineThrustNewtons = legacyAcceleration * initialMass;
				}
				if (legacyMotorBurnDuration > 0f)
				{
					profile.MainPropellantMassKg = profile.MainMassFlowKgPerSecond * legacyMotorBurnDuration;
				}
				if (legacyTurnRateDegrees > 0f)
				{
					profile.MaximumAngularRateDegreesPerSecond = legacyTurnRateDegrees;
				}
				float legacyRcsAcceleration = Mathf.Max(legacyMaximumLateralAcceleration, legacyRcsLateralAcceleration);
				if (legacyRcsAcceleration > 0f)
				{
					profile.RcsThrustNewtons = legacyRcsAcceleration * initialMass;
				}
				float legacyRcsDeltaVTotal = Mathf.Max(0f, legacyManeuverDeltaV) + Mathf.Max(0f, legacyRcsDeltaV);
				if (legacyRcsDeltaVTotal > 0f && legacyRcsAcceleration > 0f)
				{
					profile.RcsPropellantMassKg = profile.RcsMassFlowKgPerSecond * legacyRcsDeltaVTotal / legacyRcsAcceleration;
				}
				profile.SeekerActivationDelaySeconds = legacySeekerActivationDelay;
				profile.SeekerRangeMeters = legacySeekerMaximumRange;
				profile.SeekerSearchHalfAngleDegrees = legacySeekerHalfAngleDegrees;
				profile.SeekerGimbalDegrees = legacySeekerGimbalDegrees;
				profile.ProximityFuseRadiusMeters = legacyProximityFuseRadius;
				profile.ProximityFuseArmSeconds = legacyProximityFuseArmTime;
				profile.ProximityFuseSafeDistanceMeters = legacyProximityFuseSafeDistance;
				profile.WarheadLethalRadiusMeters = legacyWarheadLethalRadius;
			}
			profile = profile.Sanitized();
            passiveMemorySeconds = profile.PassiveMemorySeconds;
			dryMassKg = profile.DryMassKg;
			mainPropellantMassRemainingKg = profile.MainPropellantMassKg;
			mainEngineThrustNewtons = profile.MainEngineThrustNewtons;
			mainMassFlowKgPerSecond = profile.MainMassFlowKgPerSecond;
			propulsionMode = profile.PropulsionMode;
			secondStagePropellantMassRemainingKg = profile.SecondStagePropellantMassKg;
			secondStageThrustNewtons = profile.SecondStageThrustNewtons;
			secondStageMassFlowKgPerSecond = profile.SecondStageMassFlowKgPerSecond;
			secondStageIgnitionDelaySeconds = profile.SecondStageIgnitionDelaySeconds;
			secondStageMinimumThrottle = profile.SecondStageMinimumThrottle;
            nextSustainThrottleReview = 0;
            sustainBudgetThrottle = 0;
            pulseObservationTime = double.NegativeInfinity;
			firstStageBurnoutTime = -1f;
			secondStageIgnited = false;
            adaptiveSecondPulse=profile.AdaptiveSecondPulse&&profile.PropulsionMode==MissilePropulsionMode.DualPulseSolid;
            secondPulseDemandSeconds=0f;pulseObservationTime=double.NegativeInfinity;
            SecondPulseIgnitionReason=null;SecondPulseIgnitionTime=-1f;
			lastGuidanceRangeMeters = float.PositiveInfinity;
			rcsPropellantMassRemainingKg = profile.RcsPropellantMassKg;
			rcsThrustNewtons = profile.RcsThrustNewtons;
			rcsMassFlowKgPerSecond = profile.RcsMassFlowKgPerSecond;
			pitchYawInertiaKgM2 = profile.PitchYawInertiaKgM2;
			rcsMomentArmMeters = profile.RcsMomentArmMeters;
			maximumAngularRateRadians = profile.MaximumAngularRateDegreesPerSecond * (MathF.PI / 180f);
			currentAngularRateRadians = 0f;
			seekerActivationDelay = profile.SeekerActivationDelaySeconds;
			seekerActivated = false;
			seekerMaximumRange = profile.SeekerRangeMeters;
			seekerHalfAngleRadians = profile.SeekerSearchHalfAngleDegrees * (MathF.PI / 180f);
			seekerGimbalRadians = profile.SeekerGimbalDegrees * (MathF.PI / 180f);
			proximityFuseRadius = profile.ProximityFuseRadiusMeters;
			proximityFuseArmTime = profile.ProximityFuseArmSeconds;
			proximityFuseSafeDistance = profile.ProximityFuseSafeDistanceMeters;
			warheadLethalRadius = profile.WarheadLethalRadiusMeters;
			lifeRemaining = profile.MaximumFlightTimeSeconds;
			maximumTravelDistance = profile.MaximumRangeMeters;
		}

		private static Transform ResolveCombatantRoot(Transform source)
		{
			if (source == null)
			{
				return null;
			}
			MechPlayerRig rig = source.GetComponentInParent<MechPlayerRig>();
			if (rig != null)
			{
				return rig.transform;
			}
			CombatTestTarget target = source.GetComponentInParent<CombatTestTarget>();
			if (!(target != null))
			{
				return source;
			}
			return target.transform;
		}

		private void RegisterRadarObservers()
		{
			radarObservers.Clear();
			seekerHandle = default(MechRadarController.MissileSeekerHandle);
			irSourceRegistered = false;
			thermalState.Reset();

			if (radarController != null)
			{
				TryRegisterRadarObserver(
					radarController,
					primary: true);
			}

			var controllers = MechRadarController.ActiveControllers;
			for (var i = 0; i < controllers.Count; i++)
			{
				var observer = controllers[i];
				if (observer == null ||
					observer == radarController ||
					!observer.isActiveAndEnabled)
				{
					continue;
				}

				TryRegisterRadarObserver(
					observer,
					primary: false);
			}
		}

		private void TryRegisterRadarObserver(
			MechRadarController controller,
			bool primary)
		{
			if (controller == null)
			{
				return;
			}

			if (!controller.TryRegisterMissileSeeker(
					base.transform.position,
					velocity,
					seekerMaximumRange,
					seekerHalfAngleRadians * 2f * Mathf.Rad2Deg,
					interceptorMissile,
					out var handle,
                    source: BattlefieldEntityIdentity.GetOrAdd(gameObject)))
			{
				Debug.LogWarning(
					$"[MissileIFF] Sensor registration failed for observer '{controller.name}'.",
					this);
				return;
			}

			controller.RegisterMissileIrSource(in handle, thermalState.IntensityDb);
			LinkTerminalId friendlyReportTerminal = default;
			if (!primary && IsFriendlyRadarObserver(controller))
			{
				if (!controller.TryRegisterMissileLinkTerminal(
					base.transform.position,
					velocity,
					out friendlyReportTerminal,
                        source: BattlefieldEntityIdentity.GetOrAdd(gameObject),controlMembership:false))
				{
					Debug.LogWarning(
						$"[MissileIFF] Friendly identity terminal allocation failed for '{controller.name}'.",
						this);
				}
			}
			radarObservers.Add(new RadarObserverRegistration
			{
				Controller = controller,
				Handle = handle,
				FriendlyReportTerminal = friendlyReportTerminal,
				FriendlyReportRemaining = 0f,
			});
			irSourceRegistered = true;
			if (primary)
			{
				seekerHandle = handle;
			}
		}

		private bool IsFriendlyRadarObserver(
			MechRadarController controller)
		{
			var ownerTarget = owner != null
				? owner.GetComponentInParent<CombatTestTarget>()
				: null;
			var observerTarget = controller != null
				? controller.GetComponentInParent<CombatTestTarget>()
				: null;
            if(launchTeam.HasValue && observerTarget!=null) return launchTeam.Value==observerTarget.TeamId;
			if (ownerTarget != null && observerTarget != null)
			{
				return ownerTarget.TeamId == observerTarget.TeamId;
			}

			return owner != null &&
				   ResolveCombatantRoot(owner) ==
				   ResolveCombatantRoot(controller.transform);
		}

        private void RefreshRadarObservers()
        {
            for(var i=radarObservers.Count-1;i>=0;i--)
            {
                var registration=radarObservers[i];
                if(registration.Controller!=null && registration.Controller.isActiveAndEnabled)continue;
                if(registration.Controller!=null)
                {
                    registration.Controller.UnregisterMissileIrSource(in registration.Handle);
                    registration.Controller.UnregisterMissileSeeker(in registration.Handle);
                    if(registration.FriendlyReportTerminal.IsValid)
                        registration.Controller.UnregisterMissileLinkTerminal(registration.FriendlyReportTerminal);
                }
                radarObservers.RemoveAt(i);
            }
            foreach(var controller in MechRadarController.ActiveControllers)
            {
                if(controller==null || !controller.isActiveAndEnabled)continue;
                var present=false;
                foreach(var registration in radarObservers)if(registration.Controller==controller){present=true;break;}
                if(!present)TryRegisterRadarObserver(controller,controller==radarController);
            }
        }
		private void UpdateRadarObserverStates()
		{
			for (var i = 0; i < radarObservers.Count; i++)
			{
				var observer = radarObservers[i];
				observer.Controller?.UpdateMissileSeekerState(
					in observer.Handle,
					base.transform.position,
					velocity);
			}
		}

		private void SetRadarObserverIrIntensity(float intensityDb)
		{
			for (var i = 0; i < radarObservers.Count; i++)
			{
				var observer = radarObservers[i];
				observer.Controller?.SetMissileIrIntensity(
					in observer.Handle,
					intensityDb);
			}
		}

		private void ScheduleRadarObserverDwells(
			Vector3 direction,
			float beamWidthDegrees,
			ulong sequence)
		{
			for (var i = 0; i < radarObservers.Count; i++)
			{
				var observer = radarObservers[i];
				observer.Controller?.ScheduleMissileSeekerDwell(
					in observer.Handle,
					direction,
					beamWidthDegrees,
					sequence);
			}
		}

		private struct RadarObserverRegistration
		{
			public MechRadarController Controller;
			public MechRadarController.MissileSeekerHandle Handle;
			public LinkTerminalId FriendlyReportTerminal;
			public float FriendlyReportRemaining;
		}

		private void UpdateFriendlyObserverDataLinks(float deltaTime)
		{
			var missionRole = interceptorMissile
				? MissileMissionRole.Interceptor
				: MissileMissionRole.AirToAir;
			for (var i = 0; i < radarObservers.Count; i++)
			{
				var observer = radarObservers[i];
                if(observer.Controller==null)continue;
                if(!observer.FriendlyReportTerminal.IsValid)
                {
                    observer.FriendlyReportRemaining-=deltaTime;
                    if(observer.FriendlyReportRemaining<=0&&observer.Controller!=dataLinkController&&IsFriendlyRadarObserver(observer.Controller))
                    {
                        observer.Controller.TryRegisterMissileLinkTerminal(transform.position,velocity,out observer.FriendlyReportTerminal,source:BattlefieldEntityIdentity.GetOrAdd(gameObject),controlMembership:false);
                        observer.FriendlyReportRemaining=observer.FriendlyReportTerminal.IsValid?0:.5f;
                    }
                    radarObservers[i]=observer;
                    if(!observer.FriendlyReportTerminal.IsValid)continue;
                }

				observer.Controller.UpdateMissileLinkTerminalState(
					observer.FriendlyReportTerminal,
					base.transform.position,
					velocity);
				observer.FriendlyReportRemaining -= deltaTime;
				if (observer.FriendlyReportRemaining <= 0f)
				{
					var sent = observer.Controller.SendFriendlyMissileTrackReport(
						observer.FriendlyReportTerminal,
						base.transform.position,
						velocity,
						missionRole,missileLaunchTimestamp,knownMissileModel);
					observer.FriendlyReportRemaining = sent
						? observer.Controller.GetFriendlyMissileReportIntervalSeconds(
							missionRole)
						: 0.05f;
				}

				radarObservers[i] = observer;
			}
		}

		private void FixedUpdate()
		{
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Projectile);
            if (!configured) return;
            // Update may create a projectile between physics ticks. Integrate only
            // the portion of the first tick during which it actually existed.
            float dt=Application.isPlaying ? Mathf.Min(Time.fixedDeltaTime,(float)(Time.fixedTimeAsDouble-physicsStateTime)) : Time.fixedDeltaTime;
            if(dt<=0)return;
            previousPhysicsTime=physicsStateTime;previousPhysicsPosition=transform.position;
            if (hasCachedGuidanceTrack) guidanceMemoryRemaining = Mathf.Max(0f, (float)(guidanceMemoryExpiresAt - GuidanceTime));
			bool cannonFuseArmedAtTickStart = cannonProximityFuseEnabled && projectileFlightTime >= proximityFuseArmTime && distanceTravelled >= proximityFuseSafeDistance;
			projectileFlightTime += dt;
			lifeRemaining -= dt;
			if (lifeRemaining <= 0f)
			{
				ReportInterceptorOutcome(
					InterceptorEngagementOutcome.Missed,
					"flight_time_expired");
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			if (weaponKind == TestWeaponKind.Missile)
			{
				rcsAccelerationWorld = Vector3.zero;
				rcsAttitudeTorqueLocal = Vector3.zero;
				rcsAttitudeThrottle = 0f;
			}
			bool coldLaunchAttitudeUpdated = false;
			bool motorIgnited = (coldLaunchEnabled ? coldLaunchMotorIgnited : (launchDelay <= 0f));
			if (weaponKind == TestWeaponKind.Missile && coldLaunchEnabled && !coldLaunchMotorIgnited)
			{
				coldLaunchAttitudeUpdated = UpdateColdLaunchSequence(dt);
				motorIgnited = coldLaunchMotorIgnited;
			}
			else if (weaponKind == TestWeaponKind.Missile && launchDelay > 0f)
			{
				launchDelay = Mathf.Max(0f, launchDelay - dt);
				motorIgnited = launchDelay <= 0f;
				if (motorIgnited)
				{
					missilePhase = MissileFlightPhase.Boost;
					SetMotorVisual(active: true);
				}
			}
			if (weaponKind == TestWeaponKind.Missile)
			{
				missileFlightTime += dt;
				if (motorIgnited)
				{
					UpdateMissile(dt);
					if (!coldLaunchAttitudeUpdated)
					{
						UpdateMissileAttitude(dt);
					}
				}
				UpdateRcsThrusterVisuals(dt);
			}
			Vector3 travel = velocity * dt;
			if (weaponKind != TestWeaponKind.Missile && maximumTravelDistance > 0f && distanceTravelled + travel.magnitude >= maximumTravelDistance)
			{
				ReportInterceptorOutcome(
					InterceptorEngagementOutcome.Missed,
					"maximum_range_reached");
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			RaycastHit hit;
			bool hasSweepHit = TryFindHit(travel, out hit);
			float speed = velocity.magnitude;
			float sweepTime = ((hasSweepHit && speed > 0.001f) ? (hit.distance / speed) : float.PositiveInfinity);
			if (weaponKind == TestWeaponKind.Missile &&
				TryFindMissileProximityPass(
					dt,
					out var missileFuseTime,
					out var missileDetonationPoint,
					out var missileMissDistance,
					out var missileFuseRadius) &&
				MissileEngagementMath.ProximityOccursBeforeImpact(
					missileFuseTime,
					sweepTime))
			{
				if (interceptorMissile)
				{
					Debug.Log("[Interceptor] Proximity fuze triggered: " +
						$"CPA={missileFuseTime:0.000}s, " +
						$"miss={missileMissDistance:0.0}m, " +
						$"radius={missileFuseRadius:0.0}m, " +
						"seeker=" + (seekerLocked ? "locked" : "inertial") + ".");
				}
				DetonateWarhead(
					missileDetonationPoint,
					interceptorMissile ? missileFuseTime : 0f);
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			if (weaponKind == TestWeaponKind.Missile &&
				missedTargetDetonationPending &&
				MissileEngagementMath.ProximityOccursBeforeImpact(
					missedTargetDetonationTime,
					sweepTime))
			{
				RecordProjectileTelemetry(
					"missile_self_destruct",
					"missed",
					missedTargetDetonationReason);
				DetonateWarhead(
					missedTargetDetonationPoint,
					interceptorMissile
						? missedTargetDetonationTime
						: 0f);
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			if (cannonFuseArmedAtTickStart && TryFindCannonProximityPass(dt, out var cannonFuseTime, out var cannonDetonationPoint, out var cannonMissDistance) && cannonFuseTime <= sweepTime)
			{
				Debug.Log("[CannonFuze] proximity burst " + $"CPA={cannonFuseTime:0.000}s " + $"miss={cannonMissDistance:0.0}m " + $"trigger={proximityFuseRadius:0.0}m.", this);
				DetonateWarhead(cannonDetonationPoint);
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			if (weaponKind != TestWeaponKind.Missile && TryFindMissileClosestApproach(dt, out var interceptTarget, out var interceptTime, out var interceptPoint) && interceptTime <= sweepTime && TryApplyGunInterception(interceptTarget, interceptPoint))
			{
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			if (hasSweepHit)
			{
				HandleDirectImpact(hit);
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			base.transform.position += travel;
            physicsStateTime=Time.fixedTimeAsDouble;
			if (weaponKind == TestWeaponKind.Missile)
			{
                observerRefreshRemaining-=dt;
                if(observerRefreshRemaining<=0f){RefreshRadarObservers();observerRefreshRemaining=.25f;}
				UpdateRadarObserverStates();
				UpdateFriendlyObserverDataLinks(dt);
                if(dataLinkController!=null&&!linkTerminal.IsValid&&IsEngagementActive)
                {
                    friendlyLinkRetryRemaining-=dt;
                    if(friendlyLinkRetryRemaining<=0)
                    {
                        dataLinkController.TryRegisterMissileLinkTerminal(transform.position,velocity,out linkTerminal,source:BattlefieldEntityIdentity.GetOrAdd(gameObject));
                        friendlyLinkRetryRemaining=.5f;friendlyLinkReportRemaining=0;
                        if(linkTerminal.IsValid)dataLinkController.BindOwnedMissileTerminal(LaunchIdentity,linkTerminal);
                        if(linkTerminal.IsValid&&targetTrackId.IsValid&&!guidanceChannelReleased)dataLinkController.BeginGuidanceUplink(linkTerminal,targetTrackId,!interceptorMissile);
                    }
                }
				if (dataLinkController != null && linkTerminal.IsValid)
				{
					dataLinkController.UpdateMissileLinkTerminalState(
						linkTerminal,
						base.transform.position,
						velocity);
					friendlyLinkReportRemaining -= dt;
					if (friendlyLinkReportRemaining <= 0f)
					{
						var sent = dataLinkController.SendFriendlyMissileTrackReport(
							linkTerminal,
							base.transform.position,
							velocity,
							interceptorMissile
								? MissileMissionRole.Interceptor
								: MissileMissionRole.AirToAir,missileLaunchTimestamp,knownMissileModel);
						friendlyLinkReportRemaining = sent
							? dataLinkController.GetFriendlyMissileReportIntervalSeconds(
								interceptorMissile
									? MissileMissionRole.Interceptor
									: MissileMissionRole.AirToAir)
							: 0.05f;
					}
				}
			}
			distanceTravelled += travel.magnitude;
			if (weaponKind != TestWeaponKind.Missile && velocity.sqrMagnitude > 0.001f)
			{
				base.transform.rotation = Quaternion.LookRotation(velocity.normalized, base.transform.up);
			}
			else if (weaponKind == TestWeaponKind.Missile)
			{
				missileVisualInterpolator?.CaptureSimulationPose(
					base.transform.position,
					base.transform.rotation);
			}
		}

		private bool UpdateColdLaunchSequence(float deltaTime)
		{
			coldLaunchElapsedSeconds += deltaTime;
			Vector3 vector = base.transform.position - coldLaunchPreviousPosition;
			Vector3 carrierDisplacement = ((owner != null) ? (owner.position - coldLaunchCarrierPreviousPosition) : (coldLaunchInheritedVelocity * deltaTime));
			Vector3 relativeDisplacement = vector - carrierDisplacement;
			coldLaunchRelativeDistanceMeters += Mathf.Max(0f, Vector3.Dot(relativeDisplacement, coldLaunchAxis));
			coldLaunchPreviousPosition = base.transform.position;
			if (owner != null)
			{
				coldLaunchCarrierPreviousPosition = owner.position;
			}
			if (missilePhase == MissileFlightPhase.ColdEjection)
			{
				desiredAttitudeForward = coldLaunchAxis;
				if (coldLaunchElapsedSeconds < coldLaunchSafeSeparationSeconds || coldLaunchRelativeDistanceMeters < coldLaunchSafeSeparationMeters)
				{
					return false;
				}
                // Forward-ejected AAMs ignite on the cleared launch axis. A far
                // future intercept point is not a pre-ignition attitude command.
                // Interceptors retain their deliberate pre-ignition alignment.
                if(!interceptorMissile)
                {
                    coldLaunchMotorIgnited=true;missilePhase=MissileFlightPhase.Boost;
                    SetMotorVisual(active:true);return true;
                }
				missilePhase = MissileFlightPhase.AttitudeAlignment;
				currentAngularRateRadians = 0f;
			}
			Vector3 toIntercept = prelaunchInterceptPoint - base.transform.position;
			if (toIntercept.sqrMagnitude > 0.001f)
			{
				desiredAttitudeForward = toIntercept.normalized;
			}
			UpdateMissileAttitude(deltaTime);
			coldLaunchAlignmentTimeoutRemaining = Mathf.Max(0f, coldLaunchAlignmentTimeoutRemaining - deltaTime);
			if (Vector3.Angle(base.transform.forward, desiredAttitudeForward) * (MathF.PI / 180f) <= coldLaunchIgnitionAlignmentRadians || coldLaunchAlignmentTimeoutRemaining <= 0f)
			{
				coldLaunchMotorIgnited = true;
				missilePhase = MissileFlightPhase.Boost;
				SetMotorVisual(active: true);
			}
			return true;
		}

		private void UpdateMissile(float deltaTime)
		{
			if (terminalFuseEstimateMemoryRemaining > 0f)
			{
				terminalFuseEstimatedPosition += terminalFuseEstimatedVelocity * deltaTime;
			}
			terminalFuseEstimateMemoryRemaining = Mathf.Max(0f, terminalFuseEstimateMemoryRemaining - deltaTime);
			prelaunchSteeringRemaining = Mathf.Max(0f, prelaunchSteeringRemaining - deltaTime);
			bool firstStageActive = mainPropellantMassRemainingKg > 0.0001f && mainEngineThrustNewtons > 0f && mainMassFlowKgPerSecond > 0f;
			if (!firstStageActive && firstStageBurnoutTime < 0f)
			{
				firstStageBurnoutTime = missileFlightTime;
			}
			if (!firstStageActive &&
				!secondStageIgnited &&
				propulsionMode != MissilePropulsionMode.SinglePulseSolid &&
				secondStagePropellantMassRemainingKg > 0.0001f &&
				missileFlightTime >= firstStageBurnoutTime + secondStageIgnitionDelaySeconds &&
                ShouldIgniteSecondPulse(deltaTime))
			{
				secondStageIgnited = true;
                SecondPulseIgnitionTime=missileFlightTime;
			}
			bool secondStageActive = secondStageIgnited &&
				secondStagePropellantMassRemainingKg > 0.0001f &&
				secondStageThrustNewtons > 0f &&
				secondStageMassFlowKgPerSecond > 0f;
			bool motorActive = firstStageActive || secondStageActive;
			SetMotorVisual(motorActive);
            thermalState.Step(motorActive,deltaTime);
            thermalEmitter?.SetTemperature(thermalState.TemperatureK);
            if(irSourceRegistered)SetRadarObserverIrIntensity(thermalState.IntensityDb);
			float currentSpeed = velocity.magnitude;
			if (motorActive)
			{
				float throttle = firstStageActive ? 1f : ResolveSecondStageThrottle();
				float availablePropellant = firstStageActive
					? mainPropellantMassRemainingKg
					: secondStagePropellantMassRemainingKg;
				float massFlow = firstStageActive
					? mainMassFlowKgPerSecond
					: secondStageMassFlowKgPerSecond;
				float thrust = firstStageActive
					? mainEngineThrustNewtons
					: secondStageThrustNewtons;
				float consumedPropellant = Mathf.Min(availablePropellant, massFlow * throttle * deltaTime);
				float effectiveMass = Mathf.Max(dryMassKg, CurrentMassKg - consumedPropellant * 0.5f);
				Vector3 thrustDirection = ((base.transform.forward.sqrMagnitude > 0.001f) ? base.transform.forward.normalized : ((currentSpeed > 0.01f) ? (velocity / currentSpeed) : Vector3.forward));
				velocity += thrustDirection * (thrust * throttle / effectiveMass) * deltaTime;
				if (firstStageActive)
				{
					mainPropellantMassRemainingKg -= consumedPropellant;
				}
				else
				{
					secondStagePropellantMassRemainingKg -= consumedPropellant;
				}
			}
            // Launch-tube attitude hold must not drop already delivered guidance packets.
            // Consume them before the early return; the normal guidance path below
            // will consume any newer packets and reuse this cached observation.
            TryUpdateDatalinkTrack(deltaTime, out _);
			if (engagementState == MissileEngagementState.Spent)
			{
				desiredAttitudeForward = base.transform.forward;
				currentAngularRateRadians = 0f;
				return;
			}
			bool hasGuidance = false;
			bool refreshesTerminalEstimate = false;
            if(passiveRf&&!seekerLocked&&hasCachedGuidanceTrack&&GuidanceTime>=passiveNextListen)
            {
                passiveNextListen=GuidanceTime+.1;
                Vector3 expected=PredictRememberedTrack(in cachedGuidanceTrack)-transform.position;
                if(seekerRuntime.Listen(GuidanceTime,expected,passiveBand,passiveWaveform,out var measuredBearing))
                {
                    // A noisy angle at 200 km is not a precise lateral position.
                    // Weight it against the predicted track covariance before it
                    // drives RCS; never overwrite range or the observation epoch.
                    float range=Mathf.Max(1f,expected.magnitude);
                    float sigma=Mathf.Max(1f,(float)cachedGuidanceTrack.PredictPositionUncertainty(GuidanceTime));
                    float bearingSigma=range*Mathf.Tan(2f*Mathf.Deg2Rad);
                    float gain=sigma*sigma/(sigma*sigma+bearingSigma*bearingSigma);
                    var fused=Vector3.Slerp(expected.normalized,measuredBearing,Mathf.Clamp01(gain));
                    passiveBearing=passiveBearing.sqrMagnitude>.5f?Vector3.Slerp(passiveBearing,fused,.2f):fused;
                    passiveLastSeen=GuidanceTime;guidanceMemoryExpiresAt=GuidanceTime+GetGuidanceMemorySeconds();
                }
            }
            // Attitude hold does not mute the passive receiver.
            // AAMs hold the tube axis for 1.3 s; interceptor timing and inherited velocity are unchanged.
			if (!coldLaunchEnabled && missileFlightTime <= (interceptorMissile ? 1f : 1.3f))
            {
                desiredAttitudeForward = coldLaunchAxis;
                currentAngularRateRadians = 0f;
                return;
            }
			if (!seekerActivated &&
				missileFlightTime >= MinimumSeekerActivationDelaySeconds &&
				((!passiveRf && missileFlightTime >= seekerActivationDelay) ||
                 (passiveRf && GuidanceTime-passiveLastSeen>1.5) ||
				 IsGuidanceTrackInsideSeekerAcquisitionEnvelope()))
			{
				seekerActivated = true;
			}
			bool seekerActive = seekerActivated;
			if (seekerActive)
			{
				UpdateSeeker(deltaTime);
			}
			Vector3 targetPosition;
			Vector3 targetVelocity;
			TrackSnapshot track;
			if (seekerLocked)
			{
				missilePhase = MissileFlightPhase.TerminalGuidance;
				targetPosition = seekerEstimatedPosition;
				targetVelocity = seekerEstimatedVelocity;
				hasGuidance = true;
				refreshesTerminalEstimate = true;
				ReleaseGuidanceChannel();
			}
            else if(passiveRf&&GuidanceTime-passiveLastSeen<1.5&&passiveBearing.sqrMagnitude>.5f&&hasCachedGuidanceTrack)
            {
                float estimatedRange=Vector3.Distance(transform.position,PredictRememberedTrack(in cachedGuidanceTrack));
                targetPosition=transform.position+passiveBearing*estimatedRange;
                targetVelocity=(Vector3)(float3)cachedGuidanceTrack.Velocity;
                missilePhase=MissileFlightPhase.MidcourseCoast;hasGuidance=true;
                // Bearing guidance does not claim a new measured range or arm the fuze.
            }
			else if (TryUpdateDatalinkTrack(deltaTime, out track))
			{
				missilePhase = (motorActive ? MissileFlightPhase.Boost : (seekerActive ? MissileFlightPhase.SeekerAcquisition : MissileFlightPhase.MidcourseCoast));
				targetPosition = PredictRememberedTrack(in track);
				targetVelocity = new Vector3((float)track.Velocity.x, (float)track.Velocity.y, (float)track.Velocity.z);
				hasGuidance = true;
				refreshesTerminalEstimate = true;
			}
			else if (interceptorMissile && terminalFuseEstimateMemoryRemaining > 0f)
			{
				missilePhase = (motorActive ? MissileFlightPhase.Boost : MissileFlightPhase.MidcourseCoast);
				targetPosition = terminalFuseEstimatedPosition;
				targetVelocity = terminalFuseEstimatedVelocity;
				hasGuidance = true;
			}
			else
			{
				missilePhase = (motorActive ? MissileFlightPhase.Boost : (seekerActive ? MissileFlightPhase.SeekerAcquisition : MissileFlightPhase.Ballistic));
				targetPosition = default(Vector3);
				targetVelocity = default(Vector3);
			}
			UpdateEngagementViability(hasGuidance, targetPosition, targetVelocity, deltaTime);
			if (engagementState == MissileEngagementState.Spent)
			{
				desiredAttitudeForward = base.transform.forward;
				currentAngularRateRadians = 0f;
			}
			else if (hasGuidance)
			{
                pulseObservedPosition=targetPosition;pulseObservedVelocity=targetVelocity;pulseObservationTime=GuidanceTime;
				lastGuidanceRangeMeters = Vector3.Distance(base.transform.position, targetPosition);
				if (refreshesTerminalEstimate)
				{
					terminalFuseEstimatedPosition = seekerLocked
						? seekerFuseEstimatedPosition
						: targetPosition;
					terminalFuseEstimatedVelocity = seekerLocked
						? seekerFuseEstimatedVelocity
						: targetVelocity;
					terminalFuseEstimateMemoryRemaining = GetTerminalEstimateMemorySeconds();
				}
				Vector3 toTarget = targetPosition - base.transform.position;
				bool terminalGuidance = seekerLocked;
				float guidanceSpeed = EstimateGuidanceSpeed(toTarget.magnitude, terminalGuidance);
				float leadTimeLimit = ((!interceptorMissile) ? (terminalGuidance ? 4f : 15f) : (terminalGuidance ? 1.5f : 4f));
				Vector3 leadPoint = ToVector3(MissileEngagementMath.PredictLeadPoint(ToFloat3(base.transform.position), ToFloat3(targetPosition), ToFloat3(targetVelocity), guidanceSpeed, leadTimeLimit));
				Vector3 toAttitudeTarget = ((interceptorMissile && prelaunchSteeringRemaining > 0f) ? prelaunchInterceptPoint : leadPoint) - base.transform.position;
				if (toAttitudeTarget.sqrMagnitude > 0.001f)
				{
					desiredAttitudeForward = toAttitudeTarget.normalized;
				}
				else if (toTarget.sqrMagnitude > 0.001f)
				{
					desiredAttitudeForward = toTarget.normalized;
				}
				ApplyGuidance(targetPosition, targetVelocity, desiredAttitudeForward, terminalGuidance, deltaTime);
				EvaluateClosestApproachDetonation(
					targetPosition,
					targetVelocity,
					deltaTime);
			}
			else if (velocity.sqrMagnitude > 0.001f)
			{
				desiredAttitudeForward = velocity.normalized;
			}
		}

        private bool ShouldIgniteSecondPulse(float deltaTime)
        {
            if(!adaptiveSecondPulse){SecondPulseIgnitionReason="fixed_stage_transition";return true;}
            // Do not burn the reserve merely because time passed or a contact vanished.
            if(engagementState!=MissileEngagementState.Active||GuidanceTime-pulseObservationTime>.25)
            {secondPulseDemandSeconds=0f;return false;}
            var toTarget=pulseObservedPosition+pulseObservedVelocity*(float)(GuidanceTime-pulseObservationTime)-transform.position;
            float range=toTarget.magnitude;
            float closing=range>.01f?Vector3.Dot(velocity-pulseObservedVelocity,toTarget/range):0f;
            float turn=Vector3.Angle(velocity.sqrMagnitude>1f?velocity:transform.forward,toTarget);
            string reason=MissileSecondPulsePolicy.Evaluate(range,closing,velocity.magnitude,turn,
                secondStagePropellantMassRemainingKg/Mathf.Max(.001f,secondStageMassFlowKgPerSecond),lifeRemaining);
            secondPulseDemandSeconds=reason!=null?secondPulseDemandSeconds+deltaTime:0f;
            if(secondPulseDemandSeconds<.25f)return false;
            SecondPulseIgnitionReason=reason;return true;
        }

		private float ResolveSecondStageThrottle()
		{
			if (propulsionMode != MissilePropulsionMode.VariableFlowDuctedRocket)
			{
				return 1f;
			}

			float minimum = Mathf.Clamp(secondStageMinimumThrottle, 0.05f, 1f);
			if (float.IsInfinity(lastGuidanceRangeMeters))
			{
				return minimum;
			}

			// Preserve propellant during midcourse, then command full thrust for the terminal run.
			float terminalDemand = 1f - Mathf.InverseLerp(6_000f, 35_000f, lastGuidanceRangeMeters);
            float terminalThrottle = Mathf.Lerp(minimum, 1f, terminalDemand);
            if(!double.IsFinite(pulseObservationTime))return terminalThrottle;
            if(missileFlightTime>=nextSustainThrottleReview)
            {
                nextSustainThrottleReview=missileFlightTime+.2f;
                // Spend enough energy to arrive within the remaining service life,
                // leaving one second for terminal correction and detonation.
                double availableTime=Math.Max(.01,lifeRemaining-1);
                var toTarget=pulseObservedPosition+pulseObservedVelocity*(float)(GuidanceTime-pulseObservationTime)-transform.position;
                var futureTarget=toTarget+pulseObservedVelocity*(float)availableTime;
                var predictedDirection=futureTarget.sqrMagnitude>1?futureTarget.normalized:transform.forward;
                var budget=new MissileKinematicProfile
                {
                    EjectionSpeed=Math.Max(0,Vector3.Dot(velocity,predictedDirection)),
                    InertMass=Math.Max(.1,CurrentMassKg-secondStagePropellantMassRemainingKg),
                    MainPropellantMass=secondStagePropellantMassRemainingKg
                };
                budget.MainEngineThrust=secondStageThrustNewtons*minimum;
                budget.MainMassFlow=secondStageMassFlowKgPerSecond*minimum;
                if(budget.RelativeTravelDistance(availableTime)>=futureTarget.magnitude)
                {
                    sustainBudgetThrottle=minimum;
                    return terminalThrottle;
                }
                float low=minimum,high=1;
                for(int i=0;i<8;i++)
                {
                    float throttle=(low+high)*.5f;
                    budget.MainEngineThrust=secondStageThrustNewtons*throttle;
                    budget.MainMassFlow=secondStageMassFlowKgPerSecond*throttle;
                    if(budget.RelativeTravelDistance(availableTime)>=futureTarget.magnitude)high=throttle;
                    else low=throttle;
                }
                sustainBudgetThrottle=high;
            }
			return Mathf.Max(terminalThrottle,sustainBudgetThrottle);
		}

		private void EvaluateClosestApproachDetonation(
			Vector3 targetPosition,
			Vector3 targetVelocity,
			float deltaTime)
		{
			if (missedTargetDetonationPending ||
				engagementState != MissileEngagementState.Active ||
				missileFlightTime < proximityFuseArmTime ||
				distanceTravelled < proximityFuseSafeDistance)
			{
				return;
			}

			Vector3 relativePosition = targetPosition - base.transform.position;
			float range = relativePosition.magnitude;
			if (range <= 0.01f)
			{
				return;
			}

			Vector3 relativeVelocity = targetVelocity - velocity;
			float rangeRateNumerator = Vector3.Dot(
				relativePosition,
				relativeVelocity);
			if (rangeRateNumerator < -MinimumOpeningSpeedMetersPerSecond * range)
			{
				hasObservedClosingApproach = true;
			}

			if (!hasObservedClosingApproach)
			{
				return;
			}

			if (MissileEngagementMath.TryGetClosestApproachCrossing(
					ToFloat3(relativePosition),
					ToFloat3(relativeVelocity),
					deltaTime,
					out var closestApproachTime,
					out _))
			{
				QueueMissedTargetDetonation(
					closestApproachTime,
					base.transform.position +
						velocity * closestApproachTime,
					"closest_approach_passed");
				return;
			}

			if (rangeRateNumerator > MinimumOpeningSpeedMetersPerSecond * range)
			{
				QueueMissedTargetDetonation(
					0f,
					base.transform.position,
					"target_opening_after_pass");
			}
		}

		private void QueueMissedTargetDetonation(
			float detonationTime,
			Vector3 detonationPoint,
			string reason)
		{
			float clampedTime = Mathf.Max(0f, detonationTime);
			if (missedTargetDetonationPending &&
				clampedTime >= missedTargetDetonationTime)
			{
				return;
			}

			missedTargetDetonationPending = true;
			missedTargetDetonationTime = clampedTime;
			missedTargetDetonationPoint = detonationPoint;
			missedTargetDetonationReason = reason ?? string.Empty;
		}

		private void UpdateEngagementViability(bool hasGuidance, Vector3 targetPosition, Vector3 targetVelocity, float deltaTime)
		{
			if (engagementState != MissileEngagementState.Active || missileFlightTime < 1f)
			{
				return;
			}
			if (!hasGuidance)
			{
				engagementFailureEvidenceSeconds = 0f;
				noGuidanceSeconds += deltaTime;
				if (noGuidanceSeconds >= 2f)
				{
					MarkEngagementSpent("guidance_lost");
				}
				return;
			}
			noGuidanceSeconds = 0f;
			engagementAssessmentRemaining -= deltaTime;
			if (engagementAssessmentRemaining > 0f)
			{
				return;
			}
			engagementAssessmentRemaining = 0.2f;
			Vector3 relativePosition = targetPosition - base.transform.position;
			float range = relativePosition.magnitude;
			if (range <= Mathf.Max(warheadLethalRadius, proximityFuseRadius))
			{
				engagementFailureEvidenceSeconds = 0f;
				return;
			}
			Vector3 relativeVelocity = targetVelocity - velocity;
			if (((range > 0.01f) ? ((0f - Vector3.Dot(relativePosition, relativeVelocity)) / range) : 0f) >= -5f)
			{
				engagementFailureEvidenceSeconds = 0f;
				return;
			}
			float remainingTravelDistance = Mathf.Max(1000f, velocity.magnitude * lifeRemaining + .5f * EstimateMainEngineDeltaVRemaining() * lifeRemaining);
			float availableDeltaV = EstimateMainEngineDeltaVRemaining() + EstimateRcsDeltaVRemaining() + Mathf.Max(75f, velocity.magnitude * 0.08f);
			if (MissileEngagementMath.HasReachableIntercept(ToFloat3(relativePosition), ToFloat3(targetVelocity), ToFloat3(velocity), lifeRemaining, remainingTravelDistance, availableDeltaV))
			{
				engagementFailureEvidenceSeconds = 0f;
				return;
			}
			engagementFailureEvidenceSeconds += 0.2f;
			if (engagementFailureEvidenceSeconds >= 0.75f)
			{
				MarkEngagementSpent("intercept_unreachable");
			}
		}

		private void MarkEngagementSpent(string reason)
		{
            dataLinkController?.EndOwnedMissileLaunch(LaunchIdentity);
			if (engagementState == MissileEngagementState.Active)
			{
				QueueMissedTargetDetonation(
					0f,
					base.transform.position,
					reason);
				engagementState = MissileEngagementState.Spent;
				missilePhase = MissileFlightPhase.Ballistic;
				seekerLocked = false;
				seekerLockMemoryRemaining = 0f;
				hasCachedGuidanceTrack = false;
				guidanceMemoryRemaining = 0f;
				terminalFuseEstimateMemoryRemaining = 0f;
				rcsAccelerationWorld = Vector3.zero;
				rcsAttitudeTorqueLocal = Vector3.zero;
				rcsAttitudeThrottle = 0f;
				ReleaseGuidanceChannel();
				if (dataLinkController != null && linkTerminal.IsValid)
				{
					dataLinkController.UnregisterMissileLinkTerminal(linkTerminal);
					linkTerminal = default(LinkTerminalId);
				}
			}
		}

		private void UpdateMissileAttitude(float deltaTime)
		{
			if (desiredAttitudeForward.sqrMagnitude <= 0.001f)
			{
				currentAngularRateRadians = 0f;
				return;
			}
			Vector3 forward = desiredAttitudeForward.normalized;
			Vector3 up = Vector3.ProjectOnPlane(base.transform.up, forward);
			if (up.sqrMagnitude <= 0.001f)
			{
				up = Vector3.ProjectOnPlane(Vector3.up, forward);
			}
			if (up.sqrMagnitude <= 0.001f)
			{
				up = Vector3.ProjectOnPlane(Vector3.right, forward);
			}
			Quaternion targetRotation = Quaternion.LookRotation(forward, up.normalized);
			float angleRadians = Quaternion.Angle(base.transform.rotation, targetRotation) * (MathF.PI / 180f);
			if (angleRadians <= 0.0001f)
			{
				currentAngularRateRadians = 0f;
				base.transform.rotation = targetRotation;
				return;
			}
			float angularAcceleration = AvailableRcsAngularAcceleration();
			if (angularAcceleration <= 0f)
			{
				currentAngularRateRadians = 0f;
				return;
			}
			float stoppingLimitedRate = Mathf.Sqrt(2f * angularAcceleration * angleRadians);
			float targetAngularRate = Mathf.Min(maximumAngularRateRadians, stoppingLimitedRate);
			float previousAngularRate = currentAngularRateRadians;
			currentAngularRateRadians = Mathf.MoveTowards(currentAngularRateRadians, targetAngularRate, angularAcceleration * deltaTime);
			float turnRadians = Mathf.Min(angleRadians, currentAngularRateRadians * deltaTime);
			base.transform.rotation = Quaternion.RotateTowards(base.transform.rotation, targetRotation, turnRadians * 57.29578f);
			float rcsThrottle = Mathf.Clamp01(Mathf.Abs(currentAngularRateRadians - previousAngularRate) / Mathf.Max(angularAcceleration * deltaTime, 0.001f));
			Vector3 rotationAxisWorld = Vector3.Cross(base.transform.forward, forward);
			float angularRateDelta = currentAngularRateRadians - previousAngularRate;
			if (rcsThrottle > 0.001f && rotationAxisWorld.sqrMagnitude > 1E-06f && Mathf.Abs(angularRateDelta) > 1E-06f)
			{
				Vector3 torqueDirectionWorld = rotationAxisWorld.normalized * Mathf.Sign(angularRateDelta);
				rcsAttitudeTorqueLocal = base.transform.InverseTransformDirection(torqueDirectionWorld);
				rcsAttitudeThrottle = Mathf.Max(rcsAttitudeThrottle, rcsThrottle);
			}
			ConsumeRcsPropellant(rcsThrottle, deltaTime);
		}

		private void ApplyGuidance(Vector3 targetPosition, Vector3 targetVelocity, Vector3 guidanceDirection, bool terminal, float deltaTime)
		{
			if (velocity.sqrMagnitude <= 0.001f || rcsPropellantMassRemainingKg <= 0f || rcsThrustNewtons <= 0f)
			{
				return;
			}
			Vector3 relativePosition = targetPosition - base.transform.position;
			if (relativePosition.sqrMagnitude <= 0.001f)
			{
				return;
			}
			Vector3 relativeVelocity = targetVelocity - velocity;
			Vector3 losDirection = relativePosition.normalized;
			float closingVelocity = Mathf.Max(0f, 0f - Vector3.Dot(relativeVelocity, losDirection));
			float3 losRate = MissileEngagementMath.LineOfSightRate(ToFloat3(relativePosition), ToFloat3(relativeVelocity));
			float speed = velocity.magnitude;
			float phaseAuthority = (terminal ? 1f : 0.72f);
			float rcsAccelerationLimit = rcsThrustNewtons / Mathf.Max(CurrentMassKg, 0.001f);
			float accelerationLimit = rcsAccelerationLimit * phaseAuthority;
			if (!(accelerationLimit <= 0f))
			{
				Vector3 lateralAcceleration = Vector3.ProjectOnPlane(ToVector3(PnGuidance.Command(losRate, ToFloat3(losDirection), closingVelocity, (!interceptorMissile) ? (terminal ? 4.5f : 3.5f) : (terminal ? 5.5f : 4.5f), accelerationLimit)), velocity.normalized);
				Vector3 pursuitAcceleration = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(((guidanceDirection.sqrMagnitude > 0.001f) ? guidanceDirection.normalized : losDirection) * speed, velocity.normalized) / (terminal ? 0.65f : 1.5f), accelerationLimit * 0.45f);
				lateralAcceleration = Vector3.ClampMagnitude(lateralAcceleration + pursuitAcceleration, accelerationLimit);
				if (!(lateralAcceleration.magnitude * deltaTime <= 0f))
				{
					velocity += lateralAcceleration * deltaTime;
					rcsAccelerationWorld = lateralAcceleration;
					float rcsThrottle = Mathf.Clamp01(lateralAcceleration.magnitude / Mathf.Max(rcsAccelerationLimit * phaseAuthority, 0.001f));
					ConsumeRcsPropellant(rcsThrottle, deltaTime);
				}
			}
		}

		private float EstimateGuidanceSpeed(float targetRange, bool terminal)
		{
			float speed = Mathf.Max(velocity.magnitude, 1f);
			float propellant = mainPropellantMassRemainingKg;
			float thrust = mainEngineThrustNewtons;
			float massFlow = mainMassFlowKgPerSecond;
			if (propellant <= 0.0001f && secondStagePropellantMassRemainingKg > 0.0001f)
			{
				propellant = secondStagePropellantMassRemainingKg;
				thrust = secondStageThrustNewtons;
				massFlow = secondStageMassFlowKgPerSecond;
			}
			if (propellant <= 0.0001f || thrust <= 0f || massFlow <= 0f)
			{
				return speed;
			}
			float burnRemaining = propellant / massFlow;
			float projectionLimit = ((!interceptorMissile) ? (terminal ? 0.75f : 2.5f) : (terminal ? 0.35f : 0.8f));
			float rangeTime = targetRange / Mathf.Max(speed, 100f);
			float projectionTime = Mathf.Min(burnRemaining, projectionLimit, rangeTime);
			float axialAcceleration = thrust / Mathf.Max(CurrentMassKg, 0.001f);
			return speed + axialAcceleration * Mathf.Max(0f, projectionTime) * 0.7f;
		}

		private float AvailableRcsAngularAcceleration()
		{
			if (rcsPropellantMassRemainingKg <= 0f || rcsThrustNewtons <= 0f || rcsMomentArmMeters <= 0f || pitchYawInertiaKgM2 <= 0f)
			{
				return 0f;
			}
			return rcsThrustNewtons * rcsMomentArmMeters / pitchYawInertiaKgM2;
		}

		private float EstimateRcsDeltaVRemaining()
		{
			if (rcsPropellantMassRemainingKg <= 0f || rcsThrustNewtons <= 0f || rcsMassFlowKgPerSecond <= 0f)
			{
				return 0f;
			}
			float burnoutMass = Mathf.Max(dryMassKg + mainPropellantMassRemainingKg + secondStagePropellantMassRemainingKg, 0.001f);
			float currentMass = burnoutMass + rcsPropellantMassRemainingKg;
			return rcsThrustNewtons / rcsMassFlowKgPerSecond * Mathf.Log(currentMass / burnoutMass);
		}

		private float EstimateMainEngineDeltaVRemaining()
		{
			float deltaV = 0f;
			float inertMass = Mathf.Max(dryMassKg + rcsPropellantMassRemainingKg, 0.001f);
			if (mainPropellantMassRemainingKg > 0f && mainEngineThrustNewtons > 0f && mainMassFlowKgPerSecond > 0f)
			{
				float burnoutMass = inertMass + secondStagePropellantMassRemainingKg;
				float currentMass = burnoutMass + mainPropellantMassRemainingKg;
				deltaV += mainEngineThrustNewtons / mainMassFlowKgPerSecond * Mathf.Log(currentMass / burnoutMass);
			}
			if (secondStagePropellantMassRemainingKg > 0f && secondStageThrustNewtons > 0f && secondStageMassFlowKgPerSecond > 0f)
			{
				float currentMass = inertMass + secondStagePropellantMassRemainingKg;
				deltaV += secondStageThrustNewtons / secondStageMassFlowKgPerSecond * Mathf.Log(currentMass / inertMass);
			}
			return deltaV;
		}

		private void ConsumeRcsPropellant(float throttle, float deltaTime)
		{
			if (!(throttle <= 0f) && !(deltaTime <= 0f) && !(rcsPropellantMassRemainingKg <= 0f))
			{
				rcsPropellantMassRemainingKg = Mathf.Max(0f, rcsPropellantMassRemainingKg - rcsMassFlowKgPerSecond * Mathf.Clamp01(throttle) * deltaTime);
			}
		}

		private void UpdateSeeker(float deltaTime)
		{
			Vector3 seekerForward = ((velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward);
			bool estimateWithinGimbal = false;
			if (seekerLocked)
			{
				seekerEstimatedPosition += seekerEstimatedVelocity * deltaTime;
				seekerFuseEstimatedPosition += seekerFuseEstimatedVelocity * deltaTime;
				seekerLockMemoryRemaining -= deltaTime;
				if (seekerLockMemoryRemaining <= 0f)
				{
					seekerLocked = false;
				}
				else
				{
					Vector3 toEstimate = seekerEstimatedPosition - base.transform.position;
					estimateWithinGimbal = toEstimate.sqrMagnitude > 0.001f && PnGuidance.WithinGimbalLimit(ToFloat3(toEstimate.normalized), ToFloat3(seekerForward), seekerGimbalRadians);
				}
			}
            // Existing local lock memory must coast and expire even after the launcher disappears.
            if (seekerRuntime == null) return;
			seekerScanRemaining -= deltaTime;
			if (seekerScanRemaining > 0f)
			{
				return;
			}
			seekerScanRemaining = 0.05f;
			Vector3 searchDirection = ((seekerLocked && estimateWithinGimbal) ? (seekerEstimatedPosition - base.transform.position) : ((hasCachedGuidanceTrack) ? (PredictRememberedTrack(in cachedGuidanceTrack) - base.transform.position) : velocity));
			if (searchDirection.sqrMagnitude <= 0.001f)
			{
				searchDirection = base.transform.forward;
			}
			ScheduleRadarObserverDwells(
				searchDirection,
				seekerLocked
					? 6f
					: seekerHalfAngleRadians * Mathf.Rad2Deg * 2f,
				++seekerDwellSequence);
			int measurementCount = seekerRuntime.Scan(GuidanceTime, searchDirection,
                seekerLocked ? 6f : seekerHalfAngleRadians * Mathf.Rad2Deg * 2f,
                seekerDwellSequence, seekerMeasurementScratch);
			if (measurementCount <= 0)
			{
				return;
			}
			float bestScore = float.PositiveInfinity;
			Measurement bestMeasurement = default(Measurement);
			bool hasMeasurement = false;
			bool hasHandoff = hasCachedGuidanceTrack;
			bool hasInterceptorEstimate = interceptorTargetCommitted && terminalFuseEstimateMemoryRemaining > 0f;
			Vector3 referencePosition = (seekerLocked ? seekerEstimatedPosition : ((hasHandoff) ? PredictRememberedTrack(in cachedGuidanceTrack) : (hasInterceptorEstimate ? terminalFuseEstimatedPosition : (base.transform.position + base.transform.forward * seekerMaximumRange))));
			Vector3 referenceVelocity = (seekerLocked ? seekerEstimatedVelocity : (hasHandoff ? new Vector3((float)cachedGuidanceTrack.Velocity.x, (float)cachedGuidanceTrack.Velocity.y, (float)cachedGuidanceTrack.Velocity.z) : (hasInterceptorEstimate ? terminalFuseEstimatedVelocity : velocity)));
			for (int i = 0; i < measurementCount; i++)
			{
				Measurement measurement = seekerMeasurementScratch[i];
				if (!MissileEngagementMath.IsCompatibleSeekerTarget(interceptorMissile, measurement.ObjectClass, measurement.ClassificationConfidence) || !float.IsFinite(measurement.Range) || measurement.Range <= 0f || measurement.Timestamp <= seekerLastMeasurementTime || !PnGuidance.WithinGimbalLimit(measurement.Direction, ToFloat3(seekerForward), seekerGimbalRadians))
				{
					continue;
				}
				float measurementAge = Mathf.Max(0f, (float)(GuidanceTime - measurement.Timestamp));
				float associationError = Vector3.Distance(base.transform.position - velocity * measurementAge + ToVector3(measurement.Direction) * measurement.Range, referencePosition);
				float gate = ((!interceptorTargetCommitted) ? (seekerLocked ? 350f : 1400f) : (seekerLocked ? 100f : Mathf.Clamp(terminalFuseEstimateUncertaintyMeters * 2f + 80f, 80f, 300f)));
				if ((hasHandoff || seekerLocked || hasInterceptorEstimate) && associationError > gate)
				{
					continue;
				}
				Vector3 candidateTargetVelocity = EstimateSeekerTargetVelocity(in measurement, velocity);
				float velocityAssociationError = Vector3.Distance(candidateTargetVelocity, referenceVelocity);
				if (!interceptorTargetCommitted || (IsFinite(candidateTargetVelocity) && !(velocityAssociationError > 300f)))
				{
					float score = associationError * 2f + velocityAssociationError * 0.35f - measurement.SnrDb * 4f;
					if (!(score >= bestScore))
					{
						bestScore = score;
						bestMeasurement = measurement;
						hasMeasurement = true;
					}
				}
			}
			if (hasMeasurement)
			{
				Vector3 relativePosition = ToVector3(bestMeasurement.Direction).normalized * bestMeasurement.Range;
				Vector3 measuredTargetVelocity = EstimateSeekerTargetVelocity(in bestMeasurement, velocity);
				float selectedMeasurementAge = Mathf.Max(0f, (float)(GuidanceTime - bestMeasurement.Timestamp));
				Vector3 measuredPositionNow = base.transform.position - velocity * selectedMeasurementAge + relativePosition + measuredTargetVelocity * selectedMeasurementAge;
				var estimates = MissileEngagementMath.ResolveSeekerMeasurement(
					interceptorTargetCommitted,
					ToFloat3(referencePosition),
					ToFloat3(referenceVelocity),
					ToFloat3(measuredPositionNow),
					ToFloat3(measuredTargetVelocity));
				seekerEstimatedPosition = ToVector3(estimates.GuidancePosition);
				seekerEstimatedVelocity = ToVector3(estimates.GuidanceVelocity);
				seekerFuseEstimatedPosition = ToVector3(estimates.FusePosition);
				seekerFuseEstimatedVelocity = ToVector3(estimates.FuseVelocity);
				seekerLastMeasurementTime = bestMeasurement.Timestamp;
				seekerLocked = true;
				seekerLockMemoryRemaining = (interceptorMissile ? 0.65f : 0.35f);
			}
		}

		private static Vector3 EstimateSeekerTargetVelocity(in Measurement measurement, Vector3 sensorVelocity)
		{
			Vector3 direction = ToVector3(measurement.Direction).normalized;
			Vector3 relativePosition = direction * measurement.Range;
			float radialVelocity = (float.IsFinite(measurement.RadialVelocity) ? measurement.RadialVelocity : 0f);
			Vector3 losRate = (math.all(math.isfinite(measurement.LosRate)) ? ToVector3(measurement.LosRate) : Vector3.zero);
			return sensorVelocity + direction * radialVelocity + Vector3.Cross(losRate, relativePosition);
		}

		private static bool IsFinite(Vector3 value)
		{
			if (float.IsFinite(value.x) && float.IsFinite(value.y))
			{
				return float.IsFinite(value.z);
			}
			return false;
		}

		private bool IsGuidanceTrackInsideSeekerAcquisitionEnvelope()
		{
			if (!hasCachedGuidanceTrack ||
				guidanceMemoryRemaining <= 0f ||
				seekerMaximumRange <= 0f)
			{
				return false;
			}

			Vector3 predictedPosition =
				PredictRememberedTrack(in cachedGuidanceTrack);
			float range = Vector3.Distance(
				base.transform.position,
				predictedPosition);
			double propagationSeconds = math.max(
				0.0,
				GuidanceTime -
				cachedGuidanceTrack.LastPredictTime);
			double largestSigma = 0.0;
			for (var axis = 0; axis < 3; axis++)
			{
				double positionVariance =
					cachedGuidanceTrack.GetCovariance(axis, axis);
				double crossVariance =
					cachedGuidanceTrack.GetCovariance(axis, axis + 3);
				double velocityVariance =
					cachedGuidanceTrack.GetCovariance(axis + 3, axis + 3);
				double propagatedVariance = math.max(
					0.0,
					positionVariance +
					2.0 * propagationSeconds * crossVariance +
					propagationSeconds * propagationSeconds * velocityVariance);
				largestSigma = math.max(
					largestSigma,
					math.sqrt(propagatedVariance));
			}

			double acquisitionRange =
				FireSolutionSolver.CalculateReliableSeekerAcquisitionRange(
					seekerMaximumRange,
					largestSigma * 3.0);
			return range <= acquisitionRange;
		}

		private bool TryUpdateDatalinkTrack(float deltaTime, out TrackSnapshot track)
		{
			track = default(TrackSnapshot);
			if (!externalGuidanceSuppressed && dataLinkController != null && linkTerminal.IsValid)
			{
				int messageCount = dataLinkController.CopyLinkMessages(linkTerminal, linkInboxScratch);
				for (int i = 0; i < messageCount; i++)
				{
					LinkMessage message = linkInboxScratch[i];
					if (message.Kind == LinkMessageKind.GuidanceUpdate && message.Sequence > lastLinkSequence && (!interceptorTargetCommitted || !targetTrackId.IsValid || message.TrackPayload.TrackId.Equals(targetTrackId)))
					{
						lastLinkSequence = message.Sequence;
                        // Transport freshness is not observation freshness. Repeated
                        // packets cannot keep an old target estimate alive indefinitely.
                        if(!double.IsFinite(message.TrackPayload.LastUpdateTime)||
                            message.TrackPayload.LastUpdateTime>GuidanceTime+1e-6||
                            GuidanceTime-message.TrackPayload.LastUpdateTime>GetGuidanceMemorySeconds()||
                            hasCachedGuidanceTrack&&message.TrackPayload.LastUpdateTime<=cachedGuidanceTrack.LastUpdateTime)continue;
						cachedGuidanceTrack = message.TrackPayload;
						hasCachedGuidanceTrack = true;
						guidanceMemoryRemaining = GetGuidanceMemorySeconds();
                        guidanceMemoryExpiresAt = cachedGuidanceTrack.LastUpdateTime + GetGuidanceMemorySeconds();
					}
				}
			}
			guidanceMemoryRemaining = Mathf.Max(0f, (float)(guidanceMemoryExpiresAt - GuidanceTime));
			if (!hasCachedGuidanceTrack || guidanceMemoryRemaining <= 0f)
			{
				return false;
			}
			track = cachedGuidanceTrack;
			return true;
		}

		private float GetGuidanceMemorySeconds()
		{
			if (!interceptorTargetCommitted)
			{
				return passiveRf?Mathf.Max(4f,passiveMemorySeconds):4f;
			}
			return 0.8f;
		}

		private float GetTerminalEstimateMemorySeconds()
		{
			if (!interceptorTargetCommitted)
			{
				return 4f;
			}
			return 0.8f;
		}

		private bool TryFindMissileProximityPass(
			float deltaTime,
			out float detonationTime,
			out Vector3 detonationPoint,
			out float missDistance,
			out float effectiveFuseRadius)
		{
			detonationTime = float.PositiveInfinity;
			detonationPoint = default(Vector3);
			missDistance = float.PositiveInfinity;
			effectiveFuseRadius = proximityFuseRadius;
			if (missileFlightTime < proximityFuseArmTime || distanceTravelled < proximityFuseSafeDistance)
			{
				return false;
			}
			double now = GuidanceTime;
			if (!MissileEngagementMath.CanAuthorizeProximityFuse(
					interceptorMissile,
					seekerLocked,
					now,
					seekerLastMeasurementTime,
					InterceptorFuseMeasurementMaximumAgeSeconds))
			{
				return false;
			}
			Vector3 estimatedPosition = seekerFuseEstimatedPosition;
			Vector3 obj = seekerFuseEstimatedVelocity;
			Vector3 relativePosition = estimatedPosition - base.transform.position;
			Vector3 relativeVelocity = obj - velocity;
			float fuseLookAhead = (interceptorMissile ? Mathf.Max(deltaTime, 0.06f) : deltaTime);
			if (interceptorMissile)
			{
				effectiveFuseRadius += Mathf.Min(8f, terminalFuseEstimateUncertaintyMeters * 0.05f);
			}
			if (!MissileEngagementMath.TryGetProximityPass(
					ToFloat3(relativePosition),
					ToFloat3(relativeVelocity),
					fuseLookAhead,
					effectiveFuseRadius,
					out detonationTime,
					out missDistance))
			{
				return false;
			}
			detonationPoint = base.transform.position + velocity * detonationTime;
			return true;
		}

		private void DetonateWarhead(Vector3 detonationPoint, float predictionSeconds = 0f)
		{
            MechCombatObservationSensor.Impact(this,detonationPoint);
			if (interceptorMissile)
			{
				DetonateInterceptorWarhead(detonationPoint, predictionSeconds);
				return;
			}
			MechTestWeaponSystem.SpawnMissileExplosion(
				detonationPoint,
				(velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward,
				interceptorMissile: false);
			int colliderCount = Physics.OverlapSphereNonAlloc(detonationPoint, warheadLethalRadius, OverlapScratch, -5, QueryTriggerInteraction.Collide);
            var blastOverlaps = OverlapScratch;
            if (colliderCount == OverlapScratch.Length) { blastOverlaps = Physics.OverlapSphere(detonationPoint, warheadLethalRadius, -5, QueryTriggerInteraction.Collide); colliderCount = blastOverlaps.Length; }
			bool damagedAnyTarget = false;
			CombatTestTarget ownship = CombatTestTarget.Ownship;
			bool ownshipDamaged = false;
			BlastContactScratch.Clear();
			for (int i = 0; i < colliderCount; i++)
			{
				Collider overlap = blastOverlaps[i];
				CombatTestTarget candidate = overlap.GetComponentInParent<CombatTestTarget>();
				if (candidate == null || !candidate.IsAlive)
				{
					continue;
				}

				Vector3 contactPoint = overlap.ClosestPoint(detonationPoint);
				float distance = Vector3.Distance(detonationPoint, contactPoint);
				if (!BlastContactScratch.TryGetValue(candidate, out var contact) ||
					distance < contact.Distance)
				{
					BlastContactScratch[candidate] = new BlastContact
					{
						Point = contactPoint,
						Distance = distance,
					};
				}
			}
			foreach (var pair in BlastContactScratch)
			{
				CombatTestTarget candidate = pair.Key;
				BlastContact contact = pair.Value;
				bool damagedCandidate = TryDamageCombatTarget(
					candidate,
					contact.Point,
					contact.Distance, detonationPoint);
				damagedAnyTarget = damagedAnyTarget || damagedCandidate;
				ownshipDamaged |= damagedCandidate && candidate == ownship;
			}
			PlayOwnshipFlakAirburst(detonationPoint, ownship, ownshipDamaged);
		}

		private void PlayOwnshipFlakAirburst(Vector3 detonationPoint, CombatTestTarget ownship, bool fragmentsReached)
		{
			if (weaponKind == TestWeaponKind.Cannon && !(ownship == null) && IsHostileToOwnship(ownship))
			{
				ownship.PlayNearbyFlakAirburst(detonationPoint, fragmentsReached);
			}
		}

		private bool IsHostileToOwnship(CombatTestTarget ownship)
		{
			if (owner == null)
			{
				return true;
			}
			CombatTestTarget ownerTarget = owner.GetComponentInParent<CombatTestTarget>();
			if (ownerTarget != null)
			{
				return ownerTarget.TeamId != ownship.TeamId;
			}
			if (owner != ownship.transform && !owner.IsChildOf(ownship.transform))
			{
				return !ownship.transform.IsChildOf(owner);
			}
			return false;
		}

		private bool TryDamageCombatTarget(
			CombatTestTarget candidate,
			Vector3 contactPoint,
			float surfaceDistance, Vector3 detonationPoint)
		{
			if (candidate == null || !candidate.IsAlive)
			{
				return false;
			}
			if (surfaceDistance > warheadLethalRadius)
			{
				return false;
			}
			float falloff = Mathf.Lerp(
				0.25f,
				1f,
				1f - surfaceDistance / warheadLethalRadius);
			int previousHitCount = candidate.HitCount;
            candidate.ApplyDamage(
				damage * falloff,
				contactPoint,
				(velocity.sqrMagnitude > 0.001f)
					? velocity.normalized
					: base.transform.forward,
				weaponKind,
				effectLibrary,
				impactFfxId,
				owner, null, impactEventId, detonationPoint, warheadLethalRadius);
			return candidate.HitCount > previousHitCount;
		}

		private bool TryFindMissileClosestApproach(float deltaTime, out MechTestProjectile target, out float interceptTime, out Vector3 interceptPoint)
		{
			target = null;
			interceptTime = float.PositiveInfinity;
			interceptPoint = default(Vector3);
			if (registry.Count == 0)
			{
				return false;
			}
			float contactRadius = collisionRadius + 0.25f;
			float lethalRadius = (IsInterceptorRoundArmed() ? Mathf.Max(contactRadius, interceptorRoundLethalRadius) : contactRadius);
			float window = Mathf.Max(deltaTime, 0.0001f);
			float bestMiss = float.PositiveInfinity;
			for (int i = 0; i < registry.Count; i++)
			{
				MechTestProjectile candidate = registry[i];
				// Same predicates, cheapest first: most registry entries are gun rounds, rejected
				// by IsInterceptableMissile before the team lookup.
				if (!(candidate == null) && !(candidate == this) && candidate.IsInterceptableMissile && !candidate.IsOwnedBy(owner) && !SharesTeamWith(candidate))
				{
					Vector3 relativePosition = candidate.transform.position - base.transform.position;
					Vector3 relativeVelocity = candidate.velocity - velocity;
					float relativeSpeedSquared = relativeVelocity.sqrMagnitude;
					float cpaTime = ((relativeSpeedSquared > 0.001f) ? Mathf.Clamp((0f - Vector3.Dot(relativePosition, relativeVelocity)) / relativeSpeedSquared, 0f, window) : 0f);
					float miss = (relativePosition + relativeVelocity * cpaTime).magnitude;
					if (!(miss > lethalRadius) && (cpaTime < interceptTime || (Mathf.Approximately(cpaTime, interceptTime) && miss < bestMiss)))
					{
						target = candidate;
						interceptTime = cpaTime;
						bestMiss = miss;
						interceptPoint = base.transform.position + velocity * cpaTime;
					}
				}
			}
			return target != null;
		}

		private bool TryApplyGunInterception(MechTestProjectile target, Vector3 interceptPoint)
		{
			if (target == null)
			{
				return false;
			}
			Vector3 hitDirection = ((velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward);
			return target.ApplyPointDefenseDamage(GetPointDefenseDamage(), interceptPoint, hitDirection, weaponKind, effectLibrary, impactFfxId);
		}

		public void ConfigureInterceptorRound(float lethalRadiusMeters, float armSeconds = 0f, float safeDistanceMeters = 0f)
		{
			interceptorRoundLethalRadius = Mathf.Max(0f, lethalRadiusMeters);
			interceptorRoundArmSeconds = Mathf.Max(0f, armSeconds);
			interceptorRoundSafeDistanceMeters = Mathf.Max(0f, safeDistanceMeters);
		}

		private bool IsInterceptorRoundArmed()
		{
			if (!(interceptorRoundLethalRadius <= 0f))
			{
				if (projectileFlightTime >= interceptorRoundArmSeconds)
				{
					return distanceTravelled >= interceptorRoundSafeDistanceMeters;
				}
				return false;
			}
			return true;
		}

		private float GetPointDefenseDamage()
		{
			return damage;
		}

		public void ConfigureCannonProximityFuse(float triggerRadiusMeters, float armSeconds, float safeDistanceMeters, float lethalRadiusMeters)
		{
			if (weaponKind == TestWeaponKind.Cannon || weaponKind == TestWeaponKind.Rifle)
			{
				proximityFuseRadius = Mathf.Max(0f, triggerRadiusMeters);
				proximityFuseArmTime = Mathf.Max(0f, armSeconds);
				proximityFuseSafeDistance = Mathf.Max(0f, safeDistanceMeters);
				warheadLethalRadius = Mathf.Max(0f, lethalRadiusMeters);
				cannonProximityFuseEnabled = proximityFuseRadius > 0f && warheadLethalRadius > 0f;
			}
		}

		private bool TryFindCannonProximityPass(float deltaTime, out float detonationTime, out Vector3 detonationPoint, out float missDistance)
		{
			detonationTime = float.PositiveInfinity;
			detonationPoint = default(Vector3);
			missDistance = float.PositiveInfinity;
			if (!cannonProximityFuseEnabled || (weaponKind != TestWeaponKind.Cannon && weaponKind != TestWeaponKind.Rifle))
			{
				return false;
			}
			float searchRadius = proximityFuseRadius + velocity.magnitude * deltaTime + 40f;
			int colliderCount = Physics.OverlapSphereNonAlloc(base.transform.position, searchRadius, OverlapScratch, -5, QueryTriggerInteraction.Collide);
			float bestMissDistance = float.PositiveInfinity;
			for (int i = 0; i < colliderCount; i++)
			{
				CombatTestTarget candidate = OverlapScratch[i].GetComponentInParent<CombatTestTarget>();
				if (candidate == null || !candidate.IsAlive || IsOwnerTarget(candidate.transform))
				{
					continue;
				}
				Vector3 position = candidate.AimPoint.position;
				Vector3 targetVelocity = ResolveCombatTargetVelocity(candidate);
				Vector3 relativePosition = position - base.transform.position;
				Vector3 relativeVelocity = targetVelocity - velocity;
				if (Vector3.Dot(relativePosition, relativeVelocity) >= 0f)
				{
					continue;
				}
				CpaSolver.Result cpa = CpaSolver.Solve(new double3(relativePosition.x, relativePosition.y, relativePosition.z), new double3(relativeVelocity.x, relativeVelocity.y, relativeVelocity.z), double3.zero, Math.Max(deltaTime, 0.0001f));
				if (!(cpa.Distance > (double)proximityFuseRadius))
				{
					float cpaTime = (float)cpa.Time;
					float candidateMissDistance = (float)cpa.Distance;
					if (cpaTime < detonationTime || (Mathf.Approximately(cpaTime, detonationTime) && candidateMissDistance < bestMissDistance))
					{
						detonationTime = cpaTime;
						bestMissDistance = candidateMissDistance;
						detonationPoint = base.transform.position + velocity * cpaTime;
					}
				}
			}
			missDistance = bestMissDistance;
			return !float.IsPositiveInfinity(detonationTime);
		}

		private bool IsOwnerTarget(Transform candidate)
		{
			if (owner != null)
			{
				if (!(candidate == owner) && !candidate.IsChildOf(owner))
				{
					return owner.IsChildOf(candidate);
				}
				return true;
			}
			return false;
		}

		private static Vector3 ResolveCombatTargetVelocity(CombatTestTarget candidate)
		{
			MechFlightMotionController motion = candidate.GetComponent<MechFlightMotionController>();
			if (motion != null)
			{
				return motion.Velocity;
			}
			Rigidbody body = candidate.GetComponent<Rigidbody>();
			if (!(body != null))
			{
				return candidate.ProceduralVelocity;
			}
			return body.linearVelocity;
		}

		private void DetonateInterceptorWarhead(Vector3 detonationPoint, float predictionSeconds)
		{
            MechCombatObservationSensor.Impact(this,detonationPoint);
			MechTestWeaponSystem.SpawnMissileExplosion(
				detonationPoint,
				(velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward,
				interceptorMissile: true);
			bool damagedAnyMissile = false;
			bool destroyedAnyMissile = false;
			DamagedProjectileScratch.Clear();
			float predictionWindow = Mathf.Max(Time.fixedDeltaTime, predictionSeconds);
			int eligibleMissileCount = 0;
			float nearestPhysicalMissDistance = float.PositiveInfinity;
			for (int i = 0; i < registry.Count; i++)
			{
				MechTestProjectile candidate = registry[i];
				if (candidate == null || candidate == this || candidate.IsOwnedBy(owner) || SharesTeamWith(candidate) || !candidate.IsInterceptableMissile || !DamagedProjectileScratch.Add(candidate))
				{
					continue;
				}
				eligibleMissileCount++;
				Vector3 relativePosition = candidate.transform.position - detonationPoint;
				Vector3 relativeVelocity = candidate.velocity - velocity;
				float relativeSpeedSquared = relativeVelocity.sqrMagnitude;
				float physicalCpaTime = ((relativeSpeedSquared > 0.001f) ? Mathf.Clamp((0f - Vector3.Dot(relativePosition, relativeVelocity)) / relativeSpeedSquared, 0f, predictionWindow) : 0f);
				float distance = (relativePosition + relativeVelocity * physicalCpaTime).magnitude;
				nearestPhysicalMissDistance = Mathf.Min(nearestPhysicalMissDistance, distance);
				if (!(distance > warheadLethalRadius))
				{
					Vector3 physicalDetonationPoint = detonationPoint + velocity * physicalCpaTime;
					float falloff = Mathf.Lerp(0.45f, 1f, 1f - distance / warheadLethalRadius);
					float healthBefore = candidate.interceptionHealth;
					if (candidate.ApplyPointDefenseDamage(damage * falloff, physicalDetonationPoint, (velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward, TestWeaponKind.Missile, effectLibrary, impactFfxId))
					{
						damagedAnyMissile = true;
						destroyedAnyMissile |= !candidate.IsInterceptableMissile;
						Debug.Log("[Interceptor] Warhead damaged missile: " + $"CPA={physicalCpaTime:0.000}s, " + $"miss={distance:0.0}m, " + $"damage={damage * falloff:0.0}, " + $"health={healthBefore:0.0}->" + $"{candidate.interceptionHealth:0.0}, " + $"destroyed={!candidate.IsInterceptableMissile}.");
					}
				}
			}
			if (!damagedAnyMissile)
			{
				Debug.LogWarning("[Interceptor] Warhead detonated without a missile " + $"inside lethal radius {warheadLethalRadius:0.0}m; " + $"eligible={eligibleMissileCount}, nearest=" + $"{nearestPhysicalMissDistance:0.0}m.");
			}
			ReportInterceptorOutcome(
				destroyedAnyMissile
					? InterceptorEngagementOutcome.Destroyed
					: damagedAnyMissile
						? InterceptorEngagementOutcome.Damaged
						: InterceptorEngagementOutcome.Missed,
				"warhead_detonation");
		}

		public bool ApplyPointDefenseDamage(float incomingDamage, Vector3 hitPoint, Vector3 hitDirection, TestWeaponKind incomingWeaponKind, MechEffectPrefabLibrary incomingEffectLibrary = null, int incomingImpactFfxId = 0)
		{
			if (!IsInterceptableMissile || incomingDamage <= 0f)
			{
				return false;
			}
			interceptionHealth -= incomingDamage;
			if (interceptionHealth > 0f)
			{
				MechTestWeaponSystem.SpawnImpact(hitPoint, (hitDirection.sqrMagnitude > 0.001f) ? (-hitDirection.normalized) : (-base.transform.forward), incomingWeaponKind, incomingEffectLibrary, incomingImpactFfxId);
				return true;
			}
			interceptionResolved = true;
			Collider hitbox = GetComponent<Collider>();
			if (hitbox != null)
			{
				hitbox.enabled = false;
			}
			if (missileFlightTime >= proximityFuseArmTime && distanceTravelled >= proximityFuseSafeDistance)
			{
				DetonateWarhead(base.transform.position);
			}
			else
			{
				MechTestWeaponSystem.SpawnImpact(hitPoint, (hitDirection.sqrMagnitude > 0.001f) ? (-hitDirection.normalized) : (-base.transform.forward), incomingWeaponKind, incomingEffectLibrary, incomingImpactFfxId);
			}
			UnityEngine.Object.Destroy(base.gameObject);
			return true;
		}

		private bool TryFindHit(Vector3 travel, out RaycastHit selectedHit)
		{
			selectedHit = default(RaycastHit);
			float distance = travel.magnitude;
			if (distance <= Mathf.Epsilon)
			{
				return false;
			}
			int hitCount = Physics.SphereCastNonAlloc(base.transform.position, collisionRadius, travel / distance, SphereCastScratch, distance, -5, QueryTriggerInteraction.Collide);
            var sweepHits = SphereCastScratch;
            if (hitCount == SphereCastScratch.Length) { sweepHits = Physics.SphereCastAll(base.transform.position, collisionRadius, travel / distance, distance, -5, QueryTriggerInteraction.Collide); hitCount = sweepHits.Length; }
			float bestDistance = float.MaxValue;
			bool found = false;
			for (int i = 0; i < hitCount; i++)
			{
				RaycastHit hit = sweepHits[i];
				if (hit.distance >= bestDistance)
				{
					continue;
				}
				Transform colliderTransform = hit.collider.transform;
				if (owner != null && (colliderTransform == owner || colliderTransform.IsChildOf(owner)))
				{
					continue;
				}

			MechTestProjectile hitProjectile = hit.collider.GetComponentInParent<MechTestProjectile>();
				if (hitProjectile != null)
				{
					if (hitProjectile == this || hitProjectile.IsOwnedBy(owner) || SharesTeamWith(hitProjectile) || (weaponKind == TestWeaponKind.Missile && !interceptorMissile) || !hitProjectile.IsInterceptableMissile)
					{
						continue;
					}
				}
				else if (interceptorMissile && hit.collider.GetComponentInParent<CombatTestTarget>() != null)
				{
					continue;
				}
				bestDistance = hit.distance;
				selectedHit = hit;
				found = true;
			}
			return found;
		}

		private void HandleDirectImpact(RaycastHit hit)
		{
			if (weaponKind == TestWeaponKind.Missile)
			{
				MechTestWeaponSystem.SpawnMissileExplosion(
					hit.point,
					(velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward,
					interceptorMissile);
			}
            MechCombatObservationSensor.Impact(this,hit.point);
			MechTestProjectile hitProjectile = hit.collider.GetComponentInParent<MechTestProjectile>();
			if (hitProjectile != null && hitProjectile.ApplyPointDefenseDamage(GetPointDefenseDamage(), hit.point, (velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward, weaponKind, effectLibrary, impactFfxId))
			{
				ReportInterceptorOutcome(
					hitProjectile.IsInterceptableMissile
						? InterceptorEngagementOutcome.Damaged
						: InterceptorEngagementOutcome.Destroyed,
					"direct_impact");
				return;
			}
			CombatTestTarget hitTarget = hit.collider.GetComponentInParent<CombatTestTarget>();
			if (hitTarget != null)
			{
				if (!interceptorMissile)
				{
					hitTarget.ApplyDamage(damage, hit.point, (velocity.sqrMagnitude > 0.001f) ? velocity.normalized : base.transform.forward, weaponKind, effectLibrary, impactFfxId, owner, hit.collider, impactEventId);
					CombatTestTarget ownship = CombatTestTarget.Ownship;
					if (hitTarget == ownship)
					{
						PlayOwnshipFlakAirburst(hit.point, ownship, fragmentsReached: true);
					}
				}
			}
			else
			{
				MechTestWeaponSystem.SpawnImpact(hit.point, hit.normal, weaponKind, effectLibrary, impactFfxId);
			}
			ReportInterceptorOutcome(
				InterceptorEngagementOutcome.Missed,
				"direct_impact_non_missile");
		}

		private void ReportInterceptorOutcome(
			InterceptorEngagementOutcome outcome,
			string reason)
		{
			if (!configured || !IsInterceptorMissile || interceptorOutcomeReported)
			{
				return;
			}

			interceptorOutcomeReported = true;
			RecordProjectileTelemetry(
				"interceptor_resolved",
				outcome.ToString(),
				reason);
			InterceptorEngagementResolved?.Invoke(
				new InterceptorEngagementReport(
					owner,
					targetTrackId,
					outcome,
					reason));
		}

		private void RecordProjectileTelemetry(
			string eventName,
			string outcome,
			string reason)
		{
			var ownerTarget = owner != null
				? owner.GetComponentInParent<CombatTestTarget>()
				: null;
			var source = owner != null
				? owner.name + "#" + owner.GetEntityId()
				: "unowned_projectile";
			CombatTelemetrySession.RecordMissileProjectileEvent(
				radarController != null
					? radarController.SimTimeSeconds
					: Time.timeAsDouble,
				source,
				eventName,
				GetEntityId().ToString(),
				ownerTarget != null ? ownerTarget.TeamId : 0,
				interceptorMissile
					? MissileMissionRole.Interceptor
					: MissileMissionRole.AirToAir,
				targetTrackId,
				outcome,
				reason,
				base.transform.position,
				velocity);
		}

		private void SetMotorVisual(bool active)
		{
			if (motorVisualActive == active)
			{
				return;
			}
			motorVisualActive = active;
			missileAudio?.SetMotorActive(active);
			missilePlume?.SetBurning(active);
			if (projectileTrail != null)
			{
				projectileTrail.emitting = active;
			}
			for (int i = 0; i < propulsionParticles.Length; i++)
			{
				ParticleSystem particles = propulsionParticles[i];
				if (!(particles == null))
				{
					if (active)
					{
						particles.Play(withChildren: true);
					}
					else
					{
						particles.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
					}
				}
			}
		}

		private void CreateRcsThrusterVisuals(Transform presentationRoot)
		{
			float[] ringPositions = new float[2] { 0.78f, 0.08f };
			Vector3[] exhaustDirections = new Vector3[4]
			{
				Vector3.right,
				Vector3.up,
				Vector3.left,
				Vector3.down
			};
			GameObject root = new GameObject("Missile_RCS_Thrusters");
			Transform visualParent = presentationRoot != null ? presentationRoot : base.transform;
			root.transform.SetParent(visualParent, worldPositionStays: false);
			root.transform.localScale = presentationRoot != null
				? Vector3.one
				: new Vector3(SafeReciprocal(base.transform.localScale.x), SafeReciprocal(base.transform.localScale.y), SafeReciprocal(base.transform.localScale.z));
			rcsParticles = new ParticleSystem[8];
			rcsExhaustDirectionsLocal = new Vector3[rcsParticles.Length];
			rcsTorqueAxesLocal = new Vector3[rcsParticles.Length];
			rcsPulsePhase = new float[rcsParticles.Length];
			Material particleMaterial = GetRcsParticleMaterial();
			MechEffectThermalAppearance thermal = root.AddComponent<MechEffectThermalAppearance>();
			thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);
			int nozzleIndex = 0;
			for (int ring = 0; ring < 2; ring++)
			{
				for (int directionIndex = 0; directionIndex < 4; directionIndex++)
				{
					Vector3 exhaustDirection = exhaustDirections[directionIndex];
					GameObject obj = new GameObject($"RCS_{ring}_{directionIndex}");
					obj.transform.SetParent(root.transform, worldPositionStays: false);
					obj.transform.localPosition = exhaustDirection * 0.14f + Vector3.forward * ringPositions[ring];
					Vector3 upReference = ((Mathf.Abs(Vector3.Dot(exhaustDirection, Vector3.up)) > 0.9f) ? Vector3.forward : Vector3.up);
					obj.transform.localRotation = Quaternion.LookRotation(exhaustDirection, upReference);
					ParticleSystem particles = obj.AddComponent<ParticleSystem>();
					ConfigureRcsParticleSystem(particles, particleMaterial);
					thermal.Track(particles.GetComponent<ParticleSystemRenderer>());
					rcsPulsePhase[nozzleIndex] = (float)nozzleIndex * 0.37f % 1f;
					rcsParticles[nozzleIndex] = particles;
					rcsExhaustDirectionsLocal[nozzleIndex] = exhaustDirection;
					Vector3 momentArmDirection = ((ring == 0) ? Vector3.forward : Vector3.back);
					rcsTorqueAxesLocal[nozzleIndex] = Vector3.Cross(momentArmDirection, -exhaustDirection).normalized;
					nozzleIndex++;
				}
			}
		}

		private static void ConfigureRcsParticleSystem(ParticleSystem particles, Material particleMaterial)
		{
			ParticleSystem.MainModule main = particles.main;
			main.loop = true;
			main.playOnAwake = false;
			main.simulationSpace = ParticleSystemSimulationSpace.World;
			main.maxParticles = 64;
			main.startLifetime = new ParticleSystem.MinMaxCurve(0.05f, 0.1f);
			main.startSpeed = new ParticleSystem.MinMaxCurve(9f, 15f);
			main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.055f);
			main.startColor = Color.white;
			ParticleSystem.EmissionModule emission = particles.emission;
			emission.enabled = true;
			emission.rateOverTime = 0f;
			ParticleSystem.ShapeModule shape = particles.shape;
			shape.enabled = true;
			shape.shapeType = ParticleSystemShapeType.Cone;
			shape.angle = 26f;
			shape.radius = 0.01f;
			shape.radiusThickness = 1f;
			ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particles.sizeOverLifetime;
			sizeOverLifetime.enabled = true;
			sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.45f), new Keyframe(0.35f, 1f), new Keyframe(1f, 1.65f)));
			ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
			colorOverLifetime.enabled = true;
			colorOverLifetime.color = new ParticleSystem.MinMaxGradient(new Gradient
			{
				colorKeys = new GradientColorKey[2]
				{
					new GradientColorKey(new Color(0.9f, 0.96f, 1f), 0f),
					new GradientColorKey(Color.white, 1f)
				},
				alphaKeys = new GradientAlphaKey[3]
				{
					new GradientAlphaKey(0.95f, 0f),
					new GradientAlphaKey(0.55f, 0.35f),
					new GradientAlphaKey(0f, 1f)
				}
			});
			ParticleSystemRenderer component = particles.GetComponent<ParticleSystemRenderer>();
			component.renderMode = ParticleSystemRenderMode.Stretch;
			component.lengthScale = 0.7f;
			component.velocityScale = 0.02f;
			MechEffectResources.ConfigureUnlitRenderer(component, particleMaterial);
			particles.Play(withChildren: false);
		}

		private void UpdateRcsThrusterVisuals(float deltaTime)
		{
			if (rcsParticles.Length == 0)
			{
				return;
			}
			bool hasTranslationCommand = rcsAccelerationWorld.sqrMagnitude > 0.01f && rcsPropellantMassRemainingKg > 0f;
			bool hasAttitudeCommand = rcsAttitudeTorqueLocal.sqrMagnitude > 0.01f && rcsAttitudeThrottle > 0.001f && rcsPropellantMassRemainingKg > 0f;
			Vector3 exhaustDirectionWorld = (hasTranslationCommand ? (-rcsAccelerationWorld.normalized) : Vector3.zero);
			float accelerationFraction = (hasTranslationCommand ? Mathf.Clamp01(rcsAccelerationWorld.magnitude / Mathf.Max(rcsThrustNewtons / Mathf.Max(CurrentMassKg, 0.001f), 0.001f)) : 0f);
			for (int i = 0; i < rcsParticles.Length; i++)
			{
				ParticleSystem particles = rcsParticles[i];
				if (!(particles == null))
				{
					Vector3 nozzleDirectionWorld = base.transform.TransformDirection(rcsExhaustDirectionsLocal[i]);
					float translationAlignment = (hasTranslationCommand ? Mathf.Max(0f, Vector3.Dot(nozzleDirectionWorld, exhaustDirectionWorld)) : 0f);
					float a = ((translationAlignment > 0.12f) ? (Mathf.InverseLerp(0.12f, 1f, translationAlignment) * Mathf.Lerp(0.55f, 1f, accelerationFraction)) : 0f);
					float attitudeAlignment = ((hasAttitudeCommand && i < rcsTorqueAxesLocal.Length) ? Mathf.Max(0f, Vector3.Dot(rcsTorqueAxesLocal[i], rcsAttitudeTorqueLocal.normalized)) : 0f);
					float attitudeOutput = ((attitudeAlignment > 0.12f) ? (Mathf.InverseLerp(0.12f, 1f, attitudeAlignment) * Mathf.Lerp(0.55f, 1f, rcsAttitudeThrottle)) : 0f);
					float output = Mathf.Max(a, attitudeOutput);
					ParticleSystem.EmissionModule emission = particles.emission;
					if (output <= 0.001f)
					{
						rcsPulsePhase[i] = 0f;
						emission.rateOverTime = 0f;
					}
					else
					{
						rcsPulsePhase[i] = Mathf.Repeat(rcsPulsePhase[i] + deltaTime * 14f, 1f);
						float dutyCycle = Mathf.Lerp(0.18f, 1f, output);
						emission.rateOverTime = ((rcsPulsePhase[i] < dutyCycle) ? 260f : 0f);
					}
				}
			}
		}

		private static Material GetRcsParticleMaterial()
		{
			if (rcsParticleMaterial != null)
			{
				return rcsParticleMaterial;
			}
			rcsParticleMaterial = MechEffectResources.CreateAdditiveParticleMaterial("Runtime Missile RCS Plume", new Color(2.1f, 2.35f, 2.6f, 1f));
			return rcsParticleMaterial;
		}

		private static float SafeReciprocal(float value)
		{
			if (!(Mathf.Abs(value) > 0.0001f))
			{
				return 1f;
			}
			return 1f / value;
		}

		private void OnDestroy()
		{
            dataLinkController?.EndOwnedMissileLaunch(LaunchIdentity);
			ReleaseGuidanceChannel();
			if (missilePresentationRoot != null)
			{
				UnityEngine.Object.Destroy(missilePresentationRoot.gameObject);
				missilePresentationRoot = null;
				missileVisualInterpolator = null;
			}
            ReleaseRadarRegistrations();
        }

        private void ReleaseRadarRegistrations()
        {
            seekerRuntime?.Dispose();
            seekerRuntime = null;
			for (var i = 0; i < radarObservers.Count; i++)
			{
				var observer = radarObservers[i];
				if (observer.Controller == null)
				{
					continue;
				}

				observer.Controller.UnregisterMissileIrSource(
					in observer.Handle);
				observer.Controller.UnregisterMissileSeeker(
					in observer.Handle);
				if (observer.FriendlyReportTerminal.IsValid)
				{
					observer.Controller.UnregisterMissileLinkTerminal(
						observer.FriendlyReportTerminal);
				}
			}
			radarObservers.Clear();
			if (dataLinkController != null && linkTerminal.IsValid)
			{
				dataLinkController.UnregisterMissileLinkTerminal(linkTerminal);
			}
			linkTerminal = default(LinkTerminalId);
			seekerHandle = default(MechRadarController.MissileSeekerHandle);
		}

		private void ReleaseGuidanceChannel()
		{
			if (!guidanceChannelReleased)
			{
				radarController?.ReleaseGuidanceChannel(guidanceChannelToken);
				guidanceChannelReleased = true;
				guidanceChannelToken = 0;
			}
		}

		private static float3 ToFloat3(Vector3 value)
		{
			return new float3(value.x, value.y, value.z);
		}

		private static Vector3 ToVector3(float3 value)
		{
			return new Vector3(value.x, value.y, value.z);
		}
	}

	public enum MissileEngagementState : byte
	{
		Active,
		Spent
	}

	public enum MissileFlightPhase : byte
	{
		LaunchDelay,
		Boost,
		MidcourseCoast,
		SeekerAcquisition,
		TerminalGuidance,
		Ballistic,
		ColdEjection,
		AttitudeAlignment
	}
}
