using ACunicombat.Simulation.Driver;
using NUnit.Framework;
namespace ACunicombat.SensorBench
{
    public sealed class RadarSensorAllocatorTests
    {
        [Test] public void DeviceRolesCannotReceiveTheSameLiveAddress()
        {
            var pool=new RadarSensorAllocator();
            Assert.That(pool.TryAllocate(out var emitter),Is.True);
            Assert.That(pool.TryAllocate(out var seeker),Is.True);
            Assert.That(emitter,Is.Not.EqualTo(seeker));
            Assert.That(pool.Release(emitter),Is.True);
            Assert.That(pool.TryAllocate(out var next),Is.True);
            Assert.That(next.Value.Index,Is.EqualTo(emitter.Value.Index));
            Assert.That(next,Is.Not.EqualTo(emitter));
            Assert.That(pool.Release(emitter),Is.False);
            Assert.That(pool.Owns(next)&&pool.Owns(seeker),Is.True);
        }
        [Test] public void GenerationWrapRetiresSlotInsteadOfRevivingOldAddress()
        {
            var pool=new RadarSensorAllocator();pool.TryAllocate(out var first);var current=first;
            for(var i=0;i<300;i++){pool.Release(current);Assert.That(pool.TryAllocate(out current),Is.True);Assert.That(pool.Owns(first),Is.False);}
        }
    }
}