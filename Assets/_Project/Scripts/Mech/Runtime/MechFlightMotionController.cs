using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 物理/运动层（架构第 2 层）：消费输入快照，计算推力、速度与无滚转朝向。
    /// 不选择动画；动画根运动关闭，本组件是世界位移与 Yaw 的唯一权威。
    /// 太空机动原则：无空气阻力、有惯性、转身不等于改变速度方向——
    /// 辅助飞控实现为"期望速度跟踪"（加速度受限地趋向期望速度），
    /// assist=0 时退化为纯牛顿推力积分。
    /// </summary>
    public sealed class MechFlightMotionController : MonoBehaviour
    {
        [Header("推力")]
        [SerializeField] private float thrustAcceleration = 40f;
        [SerializeField] private float riseAcceleration = 30f;
        [SerializeField] private float maxSpeed = 80f;
        [Tooltip("辅助飞控强度 [0,1]：0=纯惯性；1=速度快速贴合输入期望。")]
        [Range(0f, 1f)] [SerializeField] private float flightAssist = 0.6f;

        [Header("Quick Boost")]
        [SerializeField] private float quickBoostImpulse = 35f;

        [Header("Assault Boost")]
        [SerializeField] private float assaultBoostAcceleration = 70f;
        [SerializeField] private float assaultBoostMaxSpeed = 140f;

        [Header("朝向")]
        [Tooltip("鼠标水平灵敏度（度/像素）。Yaw 唯一来源是鼠标，A/D 只平移。")]
        [SerializeField] private float yawSensitivity = 0.15f;
        [Tooltip("模型视觉前方相对逻辑前方(+Z)的 Yaw 修正（度）。校准模型轴用，禁止用镜头摆位掩盖。")]
        [SerializeField] private float modelForwardYawOffset;

        [Header("接地")]
        [SerializeField] private float groundY;
        [SerializeField] private float groundedEpsilon = 0.05f;

        private float yawDegrees;
        private Vector3 velocity;

        public float YawDegrees => yawDegrees;

        public Vector3 Velocity => velocity;

        public bool Grounded => transform.position.y <= groundY + groundedEpsilon && velocity.y <= 0.01f;

        /// <summary>行为图当前是否处于 AB 状态（由 Rig 依据状态名/ID 写入）。</summary>
        public bool AssaultBoostActive { get; set; }

        /// <summary>逻辑朝向（无滚转）。模型子节点应用 modelForwardYawOffset，逻辑层不含它。</summary>
        public Quaternion LogicalRotation => Quaternion.Euler(0f, yawDegrees, 0f);

        public float ModelForwardYawOffset => modelForwardYawOffset;

        public void Tick(in MechPlayerInputFrame frame, float deltaTime)
        {
            // Yaw：只来自鼠标水平；Roll 恒 0（LogicalRotation 只含 Yaw，结构性保证）。
            yawDegrees = Mathf.Repeat(yawDegrees + frame.MouseDelta.x * yawSensitivity, 360f);

            var basis = LogicalRotation;
            var desiredDir = basis * new Vector3(frame.MoveLocal.x, 0f, frame.MoveLocal.y);
            if (frame.RiseHeld)
            {
                desiredDir += Vector3.up * (riseAcceleration / thrustAcceleration);
            }

            var accel = AssaultBoostActive ? assaultBoostAcceleration : thrustAcceleration;
            var speedCap = AssaultBoostActive ? assaultBoostMaxSpeed : maxSpeed;
            if (AssaultBoostActive)
            {
                // AB：持续前向推进叠加在移动输入上；保持/退出由行为图裁决，这里只供推力。
                desiredDir += basis * Vector3.forward;
            }

            if (desiredDir.sqrMagnitude > 1f)
            {
                desiredDir.Normalize();
            }

            // 推力积分 + 辅助飞控：a = thrust·dir + assist·(v_desired − v)/dt 的受限形式。
            velocity += desiredDir * (accel * deltaTime);
            var desiredVelocity = desiredDir * speedCap;
            velocity = Vector3.Lerp(velocity, desiredVelocity,
                1f - Mathf.Exp(-flightAssist * 3f * deltaTime));
            velocity = Vector3.ClampMagnitude(velocity, speedCap);

            var position = transform.position + velocity * deltaTime;
            if (position.y < groundY)
            {
                position.y = groundY;
                velocity.y = Mathf.Max(velocity.y, 0f);
            }

            transform.SetPositionAndRotation(
                position, Quaternion.Euler(0f, yawDegrees + modelForwardYawOffset, 0f));
        }

        /// <summary>QB 物理脉冲：与行为图 QB 状态进入同一次转换触发（Rig 在 StateChanged 里调）。</summary>
        public void ApplyQuickBoostImpulse(Vector2 moveLocal)
        {
            var dir = moveLocal.sqrMagnitude > 0.0001f
                ? new Vector3(moveLocal.x, 0f, moveLocal.y).normalized
                : Vector3.forward; // 无方向输入 = 前向 QB
            velocity += LogicalRotation * dir * quickBoostImpulse;
        }
    }
}
