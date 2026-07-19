using UnityEngine;

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
        [SerializeField] private float distance = 9f;
        [SerializeField] private float height = 3f;
        [SerializeField] private float pitchSensitivity = 0.12f;
        [SerializeField] private float minPitch = -60f;
        [SerializeField] private float maxPitch = 70f;
        [SerializeField] private float followLerp = 12f;

        private float pitchDegrees = 10f;

        public float PitchDegrees => pitchDegrees;

        public void Configure(Transform target, MechFlightMotionController motionController)
        {
            followTarget = target;
            motion = motionController;
            SnapBehindTarget();
        }

        /// <summary>启动即位于机体后方、朝向机体前方（验收 6.1）。</summary>
        public void SnapBehindTarget()
        {
            if (followTarget == null || motion == null)
            {
                return;
            }

            transform.SetPositionAndRotation(ComputePosition(1f), ComputeRotation());
        }

        public void Tick(in MechPlayerInputFrame frame, float deltaTime)
        {
            if (followTarget == null || motion == null)
            {
                return;
            }

            pitchDegrees = Mathf.Clamp(
                pitchDegrees - frame.MouseDelta.y * pitchSensitivity, minPitch, maxPitch);

            var t = 1f - Mathf.Exp(-followLerp * deltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, ComputePosition(1f), t),
                ComputeRotation());
        }

        private Quaternion ComputeRotation() =>
            Quaternion.Euler(pitchDegrees, motion.YawDegrees, 0f); // Roll 恒 0

        private Vector3 ComputePosition(float blend)
        {
            var rot = ComputeRotation();
            return followTarget.position - rot * Vector3.forward * distance + Vector3.up * height;
        }
    }
}
