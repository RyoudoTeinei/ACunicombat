using System;
using Unity.Mathematics;

namespace ACunicombat.Simulation.Kinematics
{
    /// <summary>双精度运动状态（设计文档 3.2）。运动权威，PhysX 不参与飞行。</summary>
    [Serializable]
    public struct KinematicState
    {
        public double3 PositionWorld;
        public double3 VelocityWorld;
        /// <summary>高精度归一化 float 四元数（每步重归一化）。</summary>
        public quaternion Rotation;
        public float3 AngularVelocity;
        /// <summary>千克；随推进剂/弹药消耗实时变化，由机体运行状态每 Tick 写入。</summary>
        public double Mass;
    }

    /// <summary>本 Tick 输入：推进系统提交的合力/合扭矩（已经能源与热量仲裁）。</summary>
    public struct KinematicInput
    {
        public double3 ForceWorld;
        public float3 TorqueBody;
        /// <summary>本体系逆惯量张量对角（简化为主轴对角阵，装配时算出）。</summary>
        public float3 InverseInertiaDiagonal;
    }

    /// <summary>
    /// 半隐式欧拉积分（设计文档 3.2）：v += (F/m)dt; p += v·dt; ω += (τ·I⁻¹)dt;
    /// q = normalize(q + 0.5·ω⊗q·dt)。固定 100 Hz，双精度平移，无超越函数——
    /// 同平台 bit 级确定（21 节）。纯静态函数，Job/Burst 可直接调用。
    /// </summary>
    public static class SimKinematicsIntegrator
    {
        public static void Step(ref KinematicState state, in KinematicInput input, double dt)
        {
            state.VelocityWorld += input.ForceWorld * (dt / math.max(state.Mass, 1e-6));
            state.PositionWorld += state.VelocityWorld * dt;

            state.AngularVelocity += input.TorqueBody * input.InverseInertiaDiagonal * (float)dt;
            var omega = new quaternion(new float4(state.AngularVelocity, 0f));
            var dq = math.mul(omega, state.Rotation);
            state.Rotation = math.normalize(
                new quaternion(state.Rotation.value + dq.value * (0.5f * (float)dt)));
        }
    }
}
