using System;
using System.Collections.Generic;
using ACunicombat.Simulation.Kinematics;

namespace ACunicombat.Simulation.World
{
    public enum SceneEntityProjection : byte { Body, RadarMount }
    public readonly struct SceneEntityLease
    {
        internal readonly Guid Scope;
        internal readonly ulong Token;
        public readonly BattlefieldEntityId Identity;
        public readonly SimEntityId Entity;
        internal SceneEntityLease(Guid scope, ulong token, BattlefieldEntityId identity, SimEntityId entity)
        { Scope = scope; Token = token; Identity = identity; Entity = entity; }
        public bool IsValid => Scope != Guid.Empty && Token != 0 && Identity.IsValid && Entity.IsValid;
    }

    /// <summary>
    /// Transitional projection into an observer's existing SimWorld. Multiple device roles share
    /// one local entity, but each owns an idempotent lease. Disposal never retires world identity.
    /// No second motion integrator or truth state store is introduced.
    /// </summary>
    public sealed class ObserverEntityBindings : IDisposable
    {
        private sealed class Entry { public BattlefieldEntityId Identity; public SimEntityId Entity; public int References; public SceneEntityProjection Projection; }
        private readonly Guid scope = Guid.NewGuid();
        private readonly SimWorld world;
        private readonly BattlefieldEntityRegistry registry;
        private readonly Dictionary<(BattlefieldEntityId, SceneEntityProjection), Entry> entries = new Dictionary<(BattlefieldEntityId, SceneEntityProjection), Entry>();
        private readonly Dictionary<ulong, Entry> leases = new Dictionary<ulong, Entry>();
        private readonly List<ulong> stale = new List<ulong>();
        private ulong nextToken;
        private bool disposed;
        public event Action<SimEntityId> LocalEntityReleased;
        public int EntityCount => entries.Count;
        public int LeaseCount => leases.Count;
        public ObserverEntityBindings(SimWorld world, BattlefieldEntityRegistry registry)
        { this.world = world ?? throw new ArgumentNullException(nameof(world)); this.registry = registry ?? throw new ArgumentNullException(nameof(registry)); }
        public SceneEntityLease Acquire(BattlefieldEntityId identity, in KinematicState initial, SceneEntityProjection projection = SceneEntityProjection.Body)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ObserverEntityBindings));
            if (!registry.IsAlive(identity)) throw new ArgumentException("Source entity lifetime has ended.", nameof(identity));
            if (nextToken == ulong.MaxValue) throw new InvalidOperationException("Entity lease serial exhausted.");
            if (!entries.TryGetValue((identity, projection), out var entry))
            {
                entry = new Entry { Identity = identity, Projection = projection, Entity = world.Register(in initial) };
                entries.Add((identity, projection), entry);
            }
            var token = ++nextToken; leases.Add(token, entry); entry.References++;
            return new SceneEntityLease(scope, token, identity, entry.Entity);
        }
        public bool IsCurrent(in SceneEntityLease lease) => !disposed && lease.Scope == scope &&
            leases.TryGetValue(lease.Token, out var entry) && entry.Identity.Equals(lease.Identity) &&
            entry.Entity.Equals(lease.Entity) && registry.IsAlive(entry.Identity) && world.IsAlive(entry.Entity);
        public bool OwnsLocalEntity(SimEntityId entity)
        {
            foreach (var entry in entries.Values) if (entry.Entity.Equals(entity)) return true;
            return false;
        }
        public bool Release(in SceneEntityLease lease)
        {
            if (disposed || lease.Scope != scope || !leases.TryGetValue(lease.Token, out var entry) ||
                !entry.Identity.Equals(lease.Identity) || !entry.Entity.Equals(lease.Entity)) return false;
            leases.Remove(lease.Token); entry.References--;
            if (entry.References == 0)
            {
                entries.Remove((entry.Identity, entry.Projection));
                LocalEntityReleased?.Invoke(entry.Entity);
                world.Unregister(entry.Entity);
            }
            return true;
        }
        public void PruneRetired()
        {
            stale.Clear();
            foreach (var pair in leases) if (!registry.IsAlive(pair.Value.Identity)) stale.Add(pair.Key);
            foreach (var token in stale)
            {
                var entry = leases[token];
                Release(new SceneEntityLease(scope, token, entry.Identity, entry.Entity));
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            foreach (var entry in entries.Values)
            {
                LocalEntityReleased?.Invoke(entry.Entity);
                world.Unregister(entry.Entity);
            }
            leases.Clear(); entries.Clear(); disposed = true;
        }
    }
}