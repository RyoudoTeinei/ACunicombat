namespace ACunicombat.Simulation.Core
{
    /// <summary>
    /// 确定性哈希 RNG（设计文档 21）。种子构成一律用逐字段哈希链 Chain(...)，
    /// 禁止异或组合（可交换、可抵消、低位密集句柄会产生相关噪声流）。
    /// </summary>
    public static class SimHash
    {
        /// <summary>Squirrel3 单轮混淆哈希：确定性、无状态、Burst 友好。</summary>
        public static uint Squirrel3(uint position, uint seed)
        {
            const uint Noise1 = 0xB5297A4D;
            const uint Noise2 = 0x68E31DA4;
            const uint Noise3 = 0x1B56C4E9;
            var mangled = position;
            mangled *= Noise1;
            mangled += seed;
            mangled ^= mangled >> 8;
            mangled += Noise2;
            mangled ^= mangled << 8;
            mangled *= Noise3;
            mangled ^= mangled >> 8;
            return mangled;
        }

        /// <summary>哈希链组合：顺序敏感，Combine(a,b) != Combine(b,a)。</summary>
        public static uint Combine(uint seed, uint value) => Squirrel3(value, seed);

        public static uint Chain(uint seed, uint v1, uint v2) => Combine(Combine(seed, v1), v2);

        public static uint Chain(uint seed, uint v1, uint v2, uint v3) => Combine(Chain(seed, v1, v2), v3);

        public static uint Chain(uint seed, uint v1, uint v2, uint v3, uint v4) => Combine(Chain(seed, v1, v2, v3), v4);
    }

    /// <summary>由链式种子派生的确定性随机流。blittable，可放入 Job。</summary>
    public struct SimRng
    {
        private readonly uint seed;
        private uint counter;

        public SimRng(uint chainedSeed)
        {
            seed = chainedSeed;
            counter = 0;
        }

        public uint NextUInt() => SimHash.Squirrel3(counter++, seed);

        /// <summary>[0, 1) 均匀分布，24bit 精度。</summary>
        public float NextFloat01() => (NextUInt() >> 8) * (1f / 16777216f);

        /// <summary>近似标准正态（12 次均匀和，Irwin–Hall）：测量噪声用，无超越函数。</summary>
        public float NextGaussian()
        {
            var sum = 0f;
            for (var i = 0; i < 12; i++)
            {
                sum += NextFloat01();
            }

            return sum - 6f;
        }
    }
}
