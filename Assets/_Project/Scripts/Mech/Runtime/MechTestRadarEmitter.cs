using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 测试辐射源：给场景目标一台会辐射的雷达，玩家 RWR 才有东西可听。
    /// 两种模式按距离自动切换（真值侧 AI 行为，合法读 Transform）：
    ///   - 搜索：360° 圆扫（默认 120°/s，~3s 一帧），波形 3 → RWR 级别 Spiked；
    ///   - 锁定：玩家进入锁定距离后对准玩家高驻留率照射，波形 1 → 统计涌现 Locked。
    /// 模式切换靠重注册 profile 换 WaveformId——发射事件照抄 profile，无需改传感器层。
    /// 驻留走共享调度器预算（16/Tick），本组件 ≤1 条/Tick，优先级恒 WideSearch
    /// 不与玩家的 STT/TWS 抢队。SensorId 从 63 向下分配，与导弹导引头（从 1 向上）
    /// 共用 64 槽空间，两端相遇前互不冲突。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechTestRadarEmitter : MonoBehaviour
    {
        private const int IndexCeiling = 63;
        private const int IndexFloor = 48;
        private static int nextIndex = IndexCeiling;

        private const ushort SearchWaveformId = 3;
        private const ushort LockWaveformId = 1;

        [SerializeField] private float maxRangeMeters = 60_000f;
        [SerializeField] private float mainlobePowerDb = 68f;
        [SerializeField] private float sidelobeLevelDb = -28f;
        [SerializeField] private float beamWidthDegrees = 10f;
        [SerializeField] private float sweepDegreesPerSecond = 120f;
        [SerializeField] private float searchIntervalSeconds = 0.06f;
        [Tooltip("玩家进入该距离后切换为对玩家的高驻留率照射（演示锁定告警升级）。0=禁用。")]
        [SerializeField] private float lockRangeMeters = 50_000f;
        [SerializeField] private float lockIntervalSeconds = 0.04f;
        [SerializeField] private Transform designatedTarget;

        private SimulationDriver driver;
        private CombatTestTarget target;
        private Transform playerTransform;
        private SensorId emitterSensorId;
        private SimEntityId emitterEntity;
        private bool initialized;
        private bool registrationFailed;
        private bool lockModeRegistered;
        private double nextDwellTime;
        private double lastSweepTime;
        private float sweepAzimuthDegrees;
        private ulong dwellSequence;
        private Vector3 previousPosition;

        public bool HasPlayerLock =>
            initialized &&
            lockModeRegistered &&
            playerTransform != null;

        public void ConfigureTarget(
            Transform newTarget,
            float engagementRangeMeters = 50_000f)
        {
            designatedTarget = newTarget;
            playerTransform = newTarget;
            lockRangeMeters = Mathf.Clamp(
                engagementRangeMeters,
                100f,
                maxRangeMeters);
        }

        private void Awake()
        {
            target = GetComponent<CombatTestTarget>();
            playerTransform = designatedTarget;
        }

        private void FixedUpdate()
        {
            if (registrationFailed || (target != null && !target.IsAlive))
            {
                return; // 死掉的雷达不再辐射；实体注销走 OnDestroy。
            }

            if (!initialized && !TryInitialize())
            {
                return;
            }

            var dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            var position = transform.position;
            var velocity = (position - previousPosition) / dt;
            previousPosition = position;
            driver.SetEntityState(emitterEntity, new ACunicombat.Simulation.Kinematics.KinematicState
            {
                PositionWorld = new Unity.Mathematics.double3(position.x, position.y, position.z),
                VelocityWorld = new Unity.Mathematics.double3(velocity.x, velocity.y, velocity.z),
            });

            var now = driver.SimTimeSeconds;
            if (now + 1e-6 < nextDwellTime)
            {
                return;
            }

            if (playerTransform == null)
            {
                playerTransform = designatedTarget;
            }

            var lockMode = lockRangeMeters > 0f &&
                           playerTransform != null &&
                           (playerTransform.position - position).sqrMagnitude <=
                           lockRangeMeters * lockRangeMeters;
            if (lockMode != lockModeRegistered)
            {
                RegisterSensorProfile(lockMode);
                lockModeRegistered = lockMode;
            }

            Vector3 beamDirection;
            if (lockMode)
            {
                beamDirection = (playerTransform.position - position).normalized;
            }
            else
            {
                sweepAzimuthDegrees = Mathf.Repeat(
                    sweepAzimuthDegrees +
                    sweepDegreesPerSecond * (float)(now - lastSweepTime),
                    360f);
                beamDirection = Quaternion.Euler(0f, sweepAzimuthDegrees, 0f) * Vector3.forward;
            }

            lastSweepTime = now;
            driver.ScheduleRadarDwell(new Dwell
            {
                SensorId = emitterSensorId,
                DueTime = now,
                DwellTimeSeconds = lockMode ? 0.03f : 0.008f,
                BeamDirection = new Unity.Mathematics.float3(
                    beamDirection.x, beamDirection.y, beamDirection.z),
                BeamWidthRadians = beamWidthDegrees * Mathf.Deg2Rad,
                Priority = DwellPriority.WideSearch,
                Sequence = ++dwellSequence,
            });
            nextDwellTime = now + (lockMode ? lockIntervalSeconds : searchIntervalSeconds);
        }

        private bool TryInitialize()
        {
            driver = FindAnyObjectByType<SimulationDriver>();
            if (driver == null)
            {
                return false; // 玩家雷达控制器负责创建驱动器；这里只等待。
            }

            if (nextIndex < IndexFloor)
            {
                registrationFailed = true;
                Debug.LogWarning("[MechTestRadarEmitter] 测试辐射源槽位耗尽（48-63 共 16 个）。", this);
                return false;
            }

            var index = nextIndex--;
            emitterSensorId = new SensorId(new Handle(index, 1));
            previousPosition = transform.position;
            emitterEntity = driver.RegisterEntity(new ACunicombat.Simulation.Kinematics.KinematicState
            {
                PositionWorld = new Unity.Mathematics.double3(
                    transform.position.x, transform.position.y, transform.position.z),
            });
            RegisterSensorProfile(lockOnPlayer: false);
            lockModeRegistered = false;
            lastSweepTime = driver.SimTimeSeconds;
            // 相位错开：不同辐射源不在同一帧扎堆照射。
            sweepAzimuthDegrees = (index * 67f) % 360f;
            initialized = true;
            return true;
        }

        private void RegisterSensorProfile(bool lockOnPlayer)
        {
            driver.RegisterAuxiliaryRadarSensor(new RadarSensorProfile
            {
                SensorId = emitterSensorId,
                EmitterId = new EmitterId(emitterSensorId.Value),
                PlatformId = new PlatformId(emitterSensorId.Value),
                OwnerEntityId = emitterEntity,
                DwellConfig = new RadarDwellConfig
                {
                    ConstantsDb = 210f,
                    IntegrationGainDb = 6f,
                    NoisePowerDb = 0f,
                    DetectThresholdLowDb = 3f,
                    DetectThresholdHighDb = 10f,
                    RangeResolutionMeters = 15f,
                    BeamWidthRadians = beamWidthDegrees * Mathf.Deg2Rad,
                    VelocityResolutionMps = 2f,
                },
                Capability = SensorCapability.Range | SensorCapability.Doppler,
                MaxRangeMeters = maxRangeMeters,
                FrequencyBand = 1,
                WaveformId = lockOnPlayer ? LockWaveformId : SearchWaveformId,
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

        private void OnDestroy()
        {
            if (!initialized || driver == null)
            {
                return;
            }

            driver.UnregisterRadarSensor(emitterSensorId);
            driver.UnregisterEntity(emitterEntity);
        }
    }
}
