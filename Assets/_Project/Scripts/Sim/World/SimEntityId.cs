using System;
using ACunicombat.Simulation.Core;

namespace ACunicombat.Simulation.World
{
    /// <summary>
    /// 真值实体标识（设计文档 6.1 归属表）：**只定义在 World**。
    /// Contracts/Tracking/FireControl 不引用本程序集，因此该类型对它们不可见——
    /// 这是"契约结构体物理上无法包含 SimEntityId"声明成立的前提，不得移动到 Core。
    /// </summary>
    [Serializable]
    public readonly struct SimEntityId : IEquatable<SimEntityId>
    {
        public readonly Handle Value;
        public SimEntityId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        /// <summary>稳定句柄位值：RNG 种子键用（21 节，传感器仿真层有权读取）。</summary>
        public uint Packed => Value.Packed;
        public bool Equals(SimEntityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SimEntityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Entity{Value}";
    }
}
