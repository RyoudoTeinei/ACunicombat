using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ACunicombat.Simulation.World
{
    // World-only identity. Deliberately absent from Measurement and TrackSnapshot.
    public readonly struct BattlefieldEntityId : IEquatable<BattlefieldEntityId>
    {
        internal readonly Guid Scope;
        public readonly int Index;
        public readonly uint Generation;
        internal BattlefieldEntityId(Guid scope, int index, uint generation)
        { Scope = scope; Index = index; Generation = generation; }
        public bool IsValid => Scope != Guid.Empty && Generation != 0 && Index >= 0;
        public bool Equals(BattlefieldEntityId other) => Scope == other.Scope && Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is BattlefieldEntityId other && Equals(other);
        public override int GetHashCode() => unchecked((Scope.GetHashCode() * 397 ^ Index) * 397 ^ (int)Generation);
        public override string ToString() => $"{Scope:N}:{Index}:{Generation}";
    }

    /// <summary>One lifetime owner registers an entity. Observers never own or retire it.</summary>
    public sealed class BattlefieldEntityRegistry
    {
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
        private sealed class Entry { public object Owner; public uint Generation = 1; }
        private readonly Guid scope = Guid.NewGuid();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<object, BattlefieldEntityId> identities =
            new Dictionary<object, BattlefieldEntityId>(new ReferenceComparer());
        private readonly int capacity;
        public int Count => identities.Count;
        public void CopyOwners<T>(List<T> destination) where T : class
        {
            if(destination==null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            foreach(var entry in entries) if(entry.Owner is T owner) destination.Add(owner);
        }
        public BattlefieldEntityRegistry(int capacity = 4096)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }
        public BattlefieldEntityId Register(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (identities.TryGetValue(owner, out var existing)) return existing;
            var index = entries.FindIndex(entry => entry.Owner == null && entry.Generation != uint.MaxValue);
            if (index < 0)
            {
                if (entries.Count >= capacity) throw new InvalidOperationException("Battlefield identity capacity exhausted.");
                index = entries.Count; entries.Add(new Entry());
            }
            var entry = entries[index]; entry.Owner = owner;
            var id = new BattlefieldEntityId(scope, index, entry.Generation);
            identities.Add(owner, id); return id;
        }
        public bool IsAlive(BattlefieldEntityId id) => id.Scope == scope && id.IsValid &&
            id.Index < entries.Count && entries[id.Index].Owner != null && entries[id.Index].Generation == id.Generation;
        public bool Release(object owner, BattlefieldEntityId id)
        {
            if (!IsAlive(id) || !ReferenceEquals(entries[id.Index].Owner, owner)) return false;
            identities.Remove(owner); entries[id.Index].Owner = null;
            // Retire a saturated slot rather than ever making an old handle valid again.
            if (entries[id.Index].Generation != uint.MaxValue) entries[id.Index].Generation++;
            return true;
        }
    }
}