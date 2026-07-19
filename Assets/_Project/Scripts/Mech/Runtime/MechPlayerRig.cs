using System.Text;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 玩家控制编排（把架构六层接成正式闭环）：
    /// 输入适配 → 镜头/Yaw → 运动积分 → MechGraphContext → 行为图解释器（第 0 层全身）
    /// → 武器命令层（第 1 层上半身遮罩，与全身并行）。
    /// 玩家/AI/测试共享行为图入口，本组件只是"玩家输入源"的装配体——
    /// 没有任何按时间硬播动作的路径。QB 物理脉冲挂在行为图 StateChanged 上，
    /// 保证脉冲、全身动作、（后续的）特效由同一次状态转换驱动。
    /// </summary>
    public sealed class MechPlayerRig : MonoBehaviour
    {
        [SerializeField] private MechPlayerInputAdapter input;
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private MechThirdPersonCamera cam;
        [SerializeField] private MechBehaviorGraphRunner graphRunner;
        [SerializeField] private MechBodyActionPlayer actionPlayer;
        [SerializeField] private MechWeaponArmLayer weaponLayer;

        [Tooltip("キャラ制御フラグ近似：状态进入该秒数后视为全部取消窗口开放。TAE 旗标窗进 IR 后由数据替换。")]
        [SerializeField] private float cancelFlagUnlockSeconds = 0.15f;
        [Tooltip("QB 全身状态期间压制上半身武器层（官方图明确禁止前的保守近似）。")]
        [SerializeField] private bool suppressUpperDuringQuickBoost = true;
        [SerializeField] private bool showDebugOverlay = true;

        private bool[] stateIsQuickBoost = new bool[0];
        private bool[] stateIsAssaultBoost = new bool[0];
        private MechPlayerInputFrame lastFrame;

        private void Awake()
        {
            if (input == null) input = GetComponent<MechPlayerInputAdapter>();
            if (motion == null) motion = GetComponent<MechFlightMotionController>();
            if (weaponLayer == null) weaponLayer = GetComponent<MechWeaponArmLayer>();
            if (graphRunner == null) graphRunner = GetComponent<MechBehaviorGraphRunner>();

            if (graphRunner != null && graphRunner.Graph != null)
            {
                BuildStateFlags(graphRunner.Graph);
                graphRunner.StateChanged += OnGraphStateChanged;
            }

            if (weaponLayer != null && actionPlayer != null)
            {
                weaponLayer.Configure(actionPlayer);
            }

            if (cam != null && motion != null)
            {
                cam.Configure(transform, motion);
            }
        }

        private void OnDestroy()
        {
            if (graphRunner != null)
            {
                graphRunner.StateChanged -= OnGraphStateChanged;
            }
        }

        private void BuildStateFlags(MechBehaviorGraphAsset graph)
        {
            stateIsQuickBoost = new bool[graph.states.Length];
            stateIsAssaultBoost = new bool[graph.states.Length];
            for (var i = 0; i < graph.states.Length; i++)
            {
                var name = graph.states[i].officialName ?? string.Empty;
                stateIsQuickBoost[i] = name.Contains("クイックブースト");
                stateIsAssaultBoost[i] = name.Contains("ブーストダッシュ");
            }
        }

        private void OnGraphStateChanged(int from, int to)
        {
            // QB 物理脉冲与状态转换同源（验收 6.3：速度变化、动作、特效方向一致）。
            if (to < stateIsQuickBoost.Length && stateIsQuickBoost[to])
            {
                motion.ApplyQuickBoostImpulse(lastFrame.MoveLocal);
            }
        }

        private void Update()
        {
            if (input == null || motion == null || graphRunner == null)
            {
                return;
            }

            var dt = Time.deltaTime;
            lastFrame = input.Sample();

            if (cam != null)
            {
                cam.Tick(in lastFrame, dt);
            }

            motion.Tick(in lastFrame, dt);

            var current = graphRunner.CurrentStateIndex;
            motion.AssaultBoostActive =
                current >= 0 && current < stateIsAssaultBoost.Length && stateIsAssaultBoost[current];

            var grounded = motion.Grounded;
            var ccf = graphRunner.StateElapsedSeconds >= cancelFlagUnlockSeconds ? 0x1Fu : 0u;
            var ctx = new MechGraphContext
            {
                PadBits = input.BuildPadBits(in lastFrame, !grounded),
                CharControlFlags = ccf,
                Grounded = grounded,
                Airborne = !grounded,
                EnergyEmpty = false,
                LockedOn = false,
                TargetAttacking = false,
                Guarding = false,
                AttackStateType = 0,
                BodyStateType = 0,
            };
            graphRunner.Tick(in ctx, dt);

            if (weaponLayer != null)
            {
                var suppress = suppressUpperDuringQuickBoost
                               && current >= 0 && current < stateIsQuickBoost.Length
                               && stateIsQuickBoost[current];
                weaponLayer.Tick(in lastFrame, suppress, dt);
            }
        }

        private void OnGUI()
        {
            if (!showDebugOverlay || graphRunner == null)
            {
                return;
            }

            var sb = new StringBuilder(512);
            sb.AppendLine($"状态: [{graphRunner.CurrentStateIndex}] {graphRunner.CurrentStateName}  t={graphRunner.StateElapsedSeconds:F2}s");
            if (actionPlayer != null)
            {
                sb.AppendLine($"全身层: {actionPlayer.ActiveFullBodyActionId}");
                sb.AppendLine($"上半身层: {actionPlayer.ActiveUpperBodyActionId} ({weaponLayer?.Phase})");
            }

            if (motion != null)
            {
                sb.AppendLine($"速度: {motion.Velocity.magnitude:F1} m/s {motion.Velocity}");
                sb.AppendLine($"Yaw: {motion.YawDegrees:F1}°  接地: {motion.Grounded}  AB: {motion.AssaultBoostActive}");
            }

            System.Span<(int from, int to, float time)> history = stackalloc (int, int, float)[6];
            var n = graphRunner.CopyHistory(history);
            for (var i = 0; i < n; i++)
            {
                var (f, t, time) = history[i];
                var fromName = f >= 0 && graphRunner.Graph != null ? graphRunner.Graph.states[f].officialName : "-";
                var toName = graphRunner.Graph != null ? graphRunner.Graph.states[t].officialName : "?";
                sb.AppendLine($"  {time:F1}s  {fromName} → {toName}");
            }

            GUI.Label(new Rect(10, 10, 640, 320), sb.ToString());
        }
    }
}
