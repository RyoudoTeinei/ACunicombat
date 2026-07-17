using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using ACunicombat.Sensors;
using UnityEngine;

namespace ACunicombat.Simulation.Driver
{
    /// <summary>
    /// 场景级仿真驱动（设计文档 4.1）：唯一的 MonoBehaviour 入口，装配各程序集的运行时
    /// 缓冲（依赖注入在此完成——asmdef 引用图是防火墙，数据流由本类接线）。
    /// FixedUpdate 累加真实时间，while 消费整步；补算超限记性能告警，不静默降频。
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class SimulationDriver : MonoBehaviour
    {
        public const double MotionStepSeconds = 0.01;   // 100 Hz：积分、热量/能源结算
        public const double SensorStepSeconds = 0.04;   // 25 Hz：驻留消费、检测生成
        public const double FusionStepSeconds = 0.04;   // 25 Hz：关联、滤波、数据链（滞后时钟见 15.2）

        [SerializeField] private int maxMotionCatchUpTicks = 8;
        [SerializeField] private int maxSensorCatchUpTicks = 2;
        [SerializeField] private uint sessionSeed = 1;

        private FixedTickClock motionClock;
        private FixedTickClock sensorClock;
        private FixedTickClock fusionClock;

        private SimWorld world;
        private SpatialHashGrid grid;
        private DwellScheduler dwellScheduler;
        private RadarSensorSystem radar;

        public double SimTimeSeconds => motionClock.SimTimeSeconds;

        public long MotionOverrunTicks => motionClock.OverrunTicks;

        private void Awake()
        {
            motionClock = new FixedTickClock(MotionStepSeconds, maxMotionCatchUpTicks);
            sensorClock = new FixedTickClock(SensorStepSeconds, maxSensorCatchUpTicks);
            fusionClock = new FixedTickClock(FusionStepSeconds, maxSensorCatchUpTicks);

            world = new SimWorld();
            grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            dwellScheduler = new DwellScheduler(512);
            radar = new RadarSensorSystem(world, grid, dwellScheduler) { SessionSeed = sessionSeed };
        }

        private void FixedUpdate()
        {
            var dt = Time.fixedDeltaTime;

            var motionTicks = motionClock.Advance(dt);
            for (var i = 0; i < motionTicks; i++)
            {
                // TODO(阶段 0)：推进系统提交合力/合扭矩 → SimKinematicsIntegrator.Step
                // 逐实体写入 world.WriteBuffer；热量/能源结算（18 节契约）。
            }

            var sensorTicks = sensorClock.Advance(dt);
            for (var i = 0; i < sensorTicks; i++)
            {
                // TODO(阶段 4)：EW 系统先写入注入缓冲（EW 写 → Sensors 读，无回调）。
                // radar.Tick(motionClock.SimTimeSeconds);  // 阶段 1 垂直切片启用
            }

            var fusionTicks = fusionClock.Advance(dt);
            for (var i = 0; i < fusionTicks; i++)
            {
                // TODO(阶段 1)：radar.Detections → OOSM 滞后堆（15.2）→ 关联（14.4）→ CMKF。
            }

            if (motionClock.OverrunTicks > 0 && motionTicks == maxMotionCatchUpTicks)
            {
                Debug.LogWarning($"[SimDriver] motion tick overrun total={motionClock.OverrunTicks}");
            }
        }

        private void OnDestroy()
        {
            radar?.Dispose();
            dwellScheduler?.Dispose();
            grid?.Dispose();
            world?.Dispose();
        }
    }
}
