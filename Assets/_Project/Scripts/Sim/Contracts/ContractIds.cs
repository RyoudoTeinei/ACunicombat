using System;
using ACunicombat.Simulation.Core;

namespace ACunicombat.Sensing.Contracts
{
    // 标识符定义程序集归属见设计文档 6.1 归属表：本文件持有 Contracts 侧的五个类型。
    // SimEntityId 定义在 ACunicombat.Simulation.World——对本程序集类型不可见，这是真值防火墙的前提。
    // 各类型为独立 struct 包装，互不隐式转换；禁止在契约结构体中使用裸 Handle 字段。

    [Serializable]
    public readonly struct SensorId : IEquatable<SensorId>
    {
        public readonly Handle Value;
        public SensorId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(SensorId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SensorId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Sensor{Value}";
    }

    [Serializable]
    public readonly struct EmitterId : IEquatable<EmitterId>
    {
        public readonly Handle Value;
        public EmitterId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(EmitterId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EmitterId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Emitter{Value}";
    }

    [Serializable]
    public readonly struct PlatformId : IEquatable<PlatformId>
    {
        public readonly Handle Value;
        public PlatformId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(PlatformId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PlatformId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Platform{Value}";
    }

    [Serializable]
    public readonly struct TrackId : IEquatable<TrackId>
    {
        public readonly Handle Value;
        public TrackId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(TrackId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TrackId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Track{Value}";
    }

    [Serializable]
    public readonly struct NetworkTrackId : IEquatable<NetworkTrackId>
    {
        public readonly Handle Value;
        public NetworkTrackId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(NetworkTrackId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is NetworkTrackId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"NetTrack{Value}";
    }
}
