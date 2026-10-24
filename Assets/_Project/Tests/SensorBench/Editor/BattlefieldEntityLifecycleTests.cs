using System;
using ACunicombat.Simulation.World;
using ACunicombat.Simulation.Kinematics;
using NUnit.Framework;

namespace ACunicombat.SensorBench
{
    public sealed class BattlefieldEntityLifecycleTests
    {
        [Test] public void SameOwnerRegistersOnceAndForeignOwnerCannotRetireIt()
        {
            var registry = new BattlefieldEntityRegistry(); var owner = new object();
            var id = registry.Register(owner);
            Assert.That(registry.Register(owner), Is.EqualTo(id));
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.Release(new object(), id), Is.False);
            Assert.That(registry.IsAlive(id), Is.True);
        }
        [Test] public void RespawnAndRegistryScopeNeverReviveOldIdentity()
        {
            var registry = new BattlefieldEntityRegistry(); var owner = new object();
            var first = registry.Register(owner); var current = first;
            for (var i = 0; i < 300; i++)
            {
                Assert.That(registry.Release(owner, current), Is.True);
                current = registry.Register(owner);
                Assert.That(registry.IsAlive(first), Is.False);
            }
            Assert.That(new BattlefieldEntityRegistry().IsAlive(current), Is.False);
            Assert.That(registry.Release(owner, first), Is.False);
        }
        [Test] public void SeekerAndLinkShareLocalBodyButHaveIndependentLeases()
        {
            var registry = new BattlefieldEntityRegistry(); var owner = new object(); var id = registry.Register(owner);
            using var world = new SimWorld(); using var bindings = new ObserverEntityBindings(world, registry);
            var seeker = bindings.Acquire(id, default); var link = bindings.Acquire(id, default);
            Assert.That(link.Entity, Is.EqualTo(seeker.Entity));
            Assert.That(bindings.EntityCount, Is.EqualTo(1));
            Assert.That(bindings.Release(link), Is.True);
            Assert.That(bindings.Release(link), Is.False);
            Assert.That(world.IsAlive(seeker.Entity), Is.True);
            Assert.That(bindings.Release(seeker), Is.True);
            Assert.That(world.IsAlive(seeker.Entity), Is.False);
            Assert.That(registry.IsAlive(id), Is.True);
        }
        [Test] public void LauncherObserverDestructionDoesNotOwnMissileLifetime()
        {
            var registry = new BattlefieldEntityRegistry(); var launcher = new object(); var missile = new object();
            var launcherId = registry.Register(launcher); var missileId = registry.Register(missile);
            using var worldA = new SimWorld(); using var worldB = new SimWorld();
            using var a = new ObserverEntityBindings(worldA, registry); using var b = new ObserverEntityBindings(worldB, registry);
            a.Acquire(launcherId, default); var am = a.Acquire(missileId, default); var bm = b.Acquire(missileId, default);
            Assert.That(b.Release(am), Is.False, "Cross-observer lease cannot release a coincident local index.");
            registry.Release(launcher, launcherId); a.Dispose();
            Assert.That(registry.IsAlive(missileId), Is.True);
            Assert.That(b.IsCurrent(bm), Is.True);
            Assert.That(worldB.IsAlive(bm.Entity), Is.True);
        }
        [Test] public void DestroyedSourcePrunesAllLeasesAndCannotDeleteRespawn()
        {
            var registry = new BattlefieldEntityRegistry(); var owner = new object(); var id = registry.Register(owner);
            using var world = new SimWorld(); using var bindings = new ObserverEntityBindings(world, registry);
            var old = bindings.Acquire(id, default); bindings.Acquire(id, default);
            var cleanups = 0; bindings.LocalEntityReleased += _ => cleanups++;
            registry.Release(owner, id); bindings.PruneRetired();
            Assert.That(cleanups, Is.EqualTo(1)); Assert.That(bindings.LeaseCount, Is.Zero);
            var next = bindings.Acquire(registry.Register(owner), default);
            Assert.That(bindings.Release(old), Is.False);
            Assert.That(bindings.IsCurrent(next), Is.True);
        }
        [Test] public void ExhaustedRegistryFailsExplicitlyWithoutAliasing()
        {
            var registry = new BattlefieldEntityRegistry(1); var id = registry.Register(new object());
            Assert.Throws<InvalidOperationException>(() => registry.Register(new object()));
            Assert.That(registry.IsAlive(id), Is.True);
        }
        [Test] public void DeadIdentityCannotBeBoundToAnObserver()
        {
            var registry = new BattlefieldEntityRegistry(); var owner = new object(); var id = registry.Register(owner);
            using var world = new SimWorld(); using var bindings = new ObserverEntityBindings(world, registry);
            registry.Release(owner, id);
            Assert.Throws<ArgumentException>(() => bindings.Acquire(id, default));
            Assert.That(bindings.EntityCount, Is.Zero);
        }
    }
}