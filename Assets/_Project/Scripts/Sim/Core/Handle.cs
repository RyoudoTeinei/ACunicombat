using System;

namespace ACunicombat.Simulation.Core
{
    /// <summary>
    /// 通用句柄基础设施（设计文档 6.1）：Index 24bit + Generation 8bit，注销时递增 Generation。
    /// Packed == 0 保留为无效值，因此注册表的 Generation 必须从 1 起。
    /// 各领域标识符（SimEntityId、SensorId…）是对本类型的独立 struct 包装，互不隐式转换——
    /// 类型级防火墙依赖这一点，禁止在契约结构体里直接使用裸 Handle 字段（6.1 归属表）。
    /// </summary>
    [Serializable]
    public readonly struct Handle : IEquatable<Handle>
    {
        public readonly uint Packed;

        public Handle(int index, byte generation)
        {
            if ((uint)index > 0x00FFFFFF)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Packed = (uint)index | ((uint)generation << 24);
        }

        public int Index => (int)(Packed & 0x00FFFFFF);

        public byte Generation => (byte)(Packed >> 24);

        public bool IsValid => Packed != 0;

        public static readonly Handle Invalid = default;

        public bool Equals(Handle other) => Packed == other.Packed;

        public override bool Equals(object obj) => obj is Handle other && Equals(other);

        public override int GetHashCode() => (int)Packed;

        public override string ToString() => IsValid ? $"#{Index}v{Generation}" : "#invalid";

        public static bool operator ==(Handle a, Handle b) => a.Packed == b.Packed;

        public static bool operator !=(Handle a, Handle b) => a.Packed != b.Packed;
    }
}
