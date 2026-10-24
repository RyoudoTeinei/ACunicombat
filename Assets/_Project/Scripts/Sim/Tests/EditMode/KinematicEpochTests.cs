using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class KinematicEpochTests
    {
        private static KinematicState State(double t)=>new KinematicState{PositionWorld=new double3(100*t*t,0,0),VelocityWorld=new double3(200*t,0,0),Rotation=quaternion.identity,Mass=1};
        [Test]
        public void RenderAndPhysicsSamplesAreResampledToTheDeclaredEpoch()
        {
            using var world=new SimWorld();var id=world.Register(State(1));
            Assert.IsTrue(world.TrySetTimedState(id,State(1),1));Assert.IsTrue(world.TrySetTimedState(id,State(2),2));
            Assert.IsTrue(world.TrySetTimedState(id,State(2),2));
            foreach(double time in new[]{1.5,2.1,3d})
            {
                world.SwapSnapshots(time);Assert.IsTrue(world.TryGetReadState(id,out var observed));
                Assert.That(math.distance(observed.PositionWorld,State(time).PositionWorld),Is.LessThan(1e-8));
                Assert.That(math.distance(observed.VelocityWorld,State(time).VelocityWorld),Is.LessThan(1e-8));
                Assert.IsTrue(world.TryGetWriteState(id,out var latest));Assert.That(latest.PositionWorld,Is.EqualTo(State(2).PositionWorld));
            }
            Assert.That(world.TrySetTimedState(id,State(1.8),1.8),Is.False,"An older publication cannot roll the world state backwards.");
        }
        [Test]
        public void ReusedEntityDoesNotInheritOldMotionHistory()
        {
            using var world=new SimWorld();var old=world.Register(State(1));world.TrySetTimedState(old,State(1),1);world.TrySetTimedState(old,State(2),2);world.Unregister(old);
            var stationary=new KinematicState{PositionWorld=new double3(1000,0,0),Rotation=quaternion.identity,Mass=1};
            var fresh=world.Register(stationary);world.TrySetTimedState(fresh,stationary,3);world.SwapSnapshots(3.1);
            Assert.IsTrue(world.TryGetReadState(fresh,out var state));Assert.That(state.PositionWorld,Is.EqualTo(stationary.PositionWorld));Assert.That(state.VelocityWorld,Is.EqualTo(double3.zero));
        }
    }
}
