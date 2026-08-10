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
    /// Test radar emitter replicated into every observing mech's isolated
    /// simulation driver. Each observer therefore receives the same physical
    /// emissions without sharing mutable radar, RWR, tracking, or datalink IDs.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechTestRadarEmitter : MonoBehaviour
    {
        private const int MaximumObservers = 16;
        private const ushort SearchWaveformId = 3;
        private const ushort LockWaveformId = 1;

        [SerializeField] private float maxRangeMeters = 60_000f;
        [SerializeField] private float mainlobePowerDb = 68f;
        [SerializeField] private float sidelobeLevelDb = -28f;
        [SerializeField] private float beamWidthDegrees = 10f;
        [SerializeField] private float sweepDegreesPerSecond = 120f;
        [SerializeField] private float searchIntervalSeconds = 0.06f;
        [Tooltip("目标进入该距离后切换为高驻留率定向照射。0=禁用。")]
        [SerializeField] private float lockRangeMeters = 50_000f;
        [SerializeField] private float lockIntervalSeconds = 0.04f;
        private struct ObserverRegistration
        {
            public bool Active;
            public SimulationDriver Driver;
            public Transform Observer;
            public SensorId SensorId;
            public SimEntityId EntityId;
            public bool LockModeRegistered;
            public double NextDwellTime;
            public double LastSweepTime;
            public float SweepAzimuthDegrees;
            public ulong DwellSequence;
        }

        private readonly ObserverRegistration[] observers =
            new ObserverRegistration[MaximumObservers];
        private CombatTestTarget target;
        private Vector3 previousPosition;

        public void ConfigureEmissionSignature(float electromagneticSignature)
        {
            // 10 is the legacy reference loadout. Preserve its 68 dB output while
            // allowing installed parts to make the platform easier or harder to intercept.
            var signatureScale = Mathf.Max(0.01f, electromagneticSignature) / 10f;
            mainlobePowerDb = 68f + 10f * Mathf.Log10(signatureScale);
        }

        public bool HasPlayerLock
        {
            get
            {
                for (var i = 0; i < observers.Length; i++)
                {
                    if (observers[i].Active &&
                        observers[i].LockModeRegistered)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public void RegisterObserverDriver(
            SimulationDriver observerDriver,
            Transform observer,
            int sensorIndex)
        {
            if (observerDriver == null || observer == null)
            {
                return;
            }

            for (var i = 0; i < observers.Length; i++)
            {
                if (!observers[i].Active ||
                    observers[i].Driver != observerDriver)
                {
                    continue;
                }

                observers[i].Observer = observer;
                return;
            }

            for (var i = 0; i < observers.Length; i++)
            {
                if (observers[i].Active)
                {
                    continue;
                }

                var sensorId = new SensorId(
                    new Handle(Mathf.Clamp(sensorIndex, 48, 63), 1));
                var entityId = observerDriver.RegisterEntity(
                    BuildState(transform.position, Vector3.zero));
                observers[i] = new ObserverRegistration
                {
                    Active = true,
                    Driver = observerDriver,
                    Observer = observer,
                    SensorId = sensorId,
                    EntityId = entityId,
                    LastSweepTime = observerDriver.SimTimeSeconds,
                    SweepAzimuthDegrees = sensorIndex * 67f % 360f,
                };
                RegisterSensorProfile(ref observers[i], false);
                return;
            }

            Debug.LogWarning(
                "[MechTestRadarEmitter] 观察方注册容量已满。",
                this);
        }

        private void Awake()
        {
            target = GetComponent<CombatTestTarget>();
            previousPosition = transform.position;
        }

        private void FixedUpdate()
        {
            if (target != null && !target.IsAlive)
            {
                return;
            }

            var dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            var position = transform.position;
            var velocity = (position - previousPosition) / dt;
            previousPosition = position;

            for (var i = 0; i < observers.Length; i++)
            {
                if (!observers[i].Active || observers[i].Driver == null)
                {
                    continue;
                }

                ref var registration = ref observers[i];
                registration.Driver.SetEntityState(
                    registration.EntityId,
                    BuildState(position, velocity));
                ScheduleEmission(ref registration, position);
            }
        }

        private void ScheduleEmission(
            ref ObserverRegistration registration,
            Vector3 position)
        {
            var observer = registration.Observer;
            var now = registration.Driver.SimTimeSeconds;
            if (now + 1e-6 < registration.NextDwellTime)
            {
                return;
            }

            var lockMode = lockRangeMeters > 0f &&
                           observer != null &&
                           (observer.position - position).sqrMagnitude <=
                           lockRangeMeters * lockRangeMeters;
            if (lockMode != registration.LockModeRegistered)
            {
                RegisterSensorProfile(ref registration, lockMode);
                registration.LockModeRegistered = lockMode;
            }

            Vector3 beamDirection;
            if (lockMode)
            {
                beamDirection = (observer.position - position).normalized;
            }
            else
            {
                registration.SweepAzimuthDegrees = Mathf.Repeat(
                    registration.SweepAzimuthDegrees +
                    sweepDegreesPerSecond *
                    (float)(now - registration.LastSweepTime),
                    360f);
                beamDirection =
                    Quaternion.Euler(
                        0f,
                        registration.SweepAzimuthDegrees,
                        0f) * Vector3.forward;
            }

            registration.LastSweepTime = now;
            registration.Driver.ScheduleRadarDwell(new Dwell
            {
                SensorId = registration.SensorId,
                DueTime = now,
                DwellTimeSeconds = lockMode ? 0.03f : 0.008f,
                BeamDirection = new float3(
                    beamDirection.x,
                    beamDirection.y,
                    beamDirection.z),
                BeamWidthRadians = beamWidthDegrees * Mathf.Deg2Rad,
                Priority = DwellPriority.WideSearch,
                Sequence = ++registration.DwellSequence,
            });
            registration.NextDwellTime = now +
                (lockMode ? lockIntervalSeconds : searchIntervalSeconds);
        }

        private void RegisterSensorProfile(
            ref ObserverRegistration registration,
            bool lockOnObserver)
        {
            registration.Driver.RegisterAuxiliaryRadarSensor(
                new RadarSensorProfile
                {
                    SensorId = registration.SensorId,
                    EmitterId = new EmitterId(
                        registration.SensorId.Value),
                    PlatformId = new PlatformId(
                        registration.SensorId.Value),
                    OwnerEntityId = registration.EntityId,
                    DwellConfig = new RadarDwellConfig
                    {
                        ConstantsDb = 210f,
                        IntegrationGainDb = 6f,
                        NoisePowerDb = 0f,
                        DetectThresholdLowDb = 3f,
                        DetectThresholdHighDb = 10f,
                        RangeResolutionMeters = 15f,
                        BeamWidthRadians =
                            beamWidthDegrees * Mathf.Deg2Rad,
                        VelocityResolutionMps = 2f,
                    },
                    Capability =
                        SensorCapability.Range |
                        SensorCapability.Doppler,
                    MaxRangeMeters = maxRangeMeters,
                    FrequencyBand = 1,
                    WaveformId = lockOnObserver
                        ? LockWaveformId
                        : SearchWaveformId,
                    MainlobePowerDb = mainlobePowerDb,
                    SidelobeLevelDb = sidelobeLevelDb,
                    ClutterPowerDb = -45f,
                    Resolutions = new ResolutionMerge.Resolutions
                    {
                        RangeMeters = 15f,
                        AngleRadians = 1.5f * Mathf.Deg2Rad,
                        VelocityMps = 2f,
                    },
                });
        }

        private static KinematicState BuildState(
            Vector3 position,
            Vector3 velocity)
        {
            return new KinematicState
            {
                PositionWorld = new double3(
                    position.x,
                    position.y,
                    position.z),
                VelocityWorld = new double3(
                    velocity.x,
                    velocity.y,
                    velocity.z),
            };
        }

        private void OnDestroy()
        {
            for (var i = 0; i < observers.Length; i++)
            {
                if (!observers[i].Active || observers[i].Driver == null)
                {
                    continue;
                }

                observers[i].Driver.UnregisterRadarSensor(
                    observers[i].SensorId);
                observers[i].Driver.UnregisterEntity(
                    observers[i].EntityId);
                observers[i] = default;
            }
        }
    }
}
