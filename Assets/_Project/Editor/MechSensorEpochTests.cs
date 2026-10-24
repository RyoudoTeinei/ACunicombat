using System.Collections;
using System.Reflection;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using ACunicombat.Simulation.Kinematics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using NUnit.Framework;
public sealed class MechSensorEpochTests
{
    [UnityTest] public IEnumerator RenderPublishedAccelerationMatchesSensorEpochAtThirtyFps()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float capture=Time.captureDeltaTime;Time.captureDeltaTime=1f/30;
        var go=new GameObject("Render epoch source");var driver=go.AddComponent<SimulationDriver>();
        var mover=go.AddComponent<SensorEpochRenderProducer>();mover.Driver=driver;
        mover.Entity=driver.RegisterEntity(SensorEpochPhysicsProducer.StateAt(Time.timeAsDouble));
        var world=(SimWorld)typeof(SimulationDriver).GetField("world",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(driver);
        double start=Time.fixedTimeAsDouble,last=-1;int samples=0;
        try
        {
            while(Time.fixedTimeAsDouble<start+.5)
            {
                yield return null;
                if(world.SnapshotTime<=start+.15||world.SnapshotTime==last)continue;
                last=world.SnapshotTime;Assert.IsTrue(world.TryGetReadState(mover.Entity,out var observed));
                var expected=SensorEpochPhysicsProducer.StateAt(last);
                Assert.That(math.distance(observed.PositionWorld,expected.PositionWorld),Is.LessThan(.0001));
                Assert.That(math.distance(observed.VelocityWorld,expected.VelocityWorld),Is.LessThan(.0001));samples++;
            }
            Assert.That(samples,Is.GreaterThanOrEqualTo(3));
        }
        finally{Time.captureDeltaTime=capture;Object.Destroy(go);}
        yield return null;yield return new ExitPlayMode();
    }

    [UnityTest] public IEnumerator AcceleratingPhysicsPublicationMatchesDeclaredSensorEpoch()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        yield return new EnterPlayMode();
        var go=new GameObject("Sensor epoch source");var driver=go.AddComponent<SimulationDriver>();
        var mover=go.AddComponent<SensorEpochPhysicsProducer>();mover.Driver=driver;
        mover.Entity=driver.RegisterEntity(SensorEpochPhysicsProducer.StateAt(Time.fixedTimeAsDouble));
        var world=(SimWorld)typeof(SimulationDriver).GetField("world",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(driver);
        double start=Time.fixedTimeAsDouble,last=-1;int samples=0;
        try
        {
            while(Time.fixedTimeAsDouble<start+.35)
            {
                yield return new WaitForFixedUpdate();
                if(world.SnapshotTime<=start+.06 || world.SnapshotTime==last)continue;
                last=world.SnapshotTime;Assert.IsTrue(world.TryGetReadState(mover.Entity,out var observed));
                var expected=SensorEpochPhysicsProducer.StateAt(world.SnapshotTime);
                Assert.That(math.distance(observed.PositionWorld,expected.PositionWorld),Is.LessThan(.0001));
                Assert.That(math.distance(observed.VelocityWorld,expected.VelocityWorld),Is.LessThan(.0001));samples++;
            }
            Assert.That(samples,Is.GreaterThanOrEqualTo(3));
        }
        finally {Object.Destroy(go);}
        yield return null;yield return new ExitPlayMode();
    }
    [UnityTearDown] public IEnumerator LeavePlayMode(){if(Application.isPlaying)yield return new ExitPlayMode();}
}
public sealed class SensorEpochRenderProducer : MonoBehaviour
{
    public SimulationDriver Driver;public SimEntityId Entity;
    private void Update(){Driver.SetTimedEntityState(Entity,SensorEpochPhysicsProducer.StateAt(Time.timeAsDouble),Time.timeAsDouble);}
}
public sealed class SensorEpochPhysicsProducer : MonoBehaviour
{
    public SimulationDriver Driver;public SimEntityId Entity;
    public static KinematicState StateAt(double t)=>new KinematicState{PositionWorld=new double3(100*t*t,0,1000),VelocityWorld=new double3(200*t,0,0),Rotation=quaternion.identity,Mass=1};
    private void FixedUpdate(){Driver.SetTimedEntityState(Entity,StateAt(Time.fixedTimeAsDouble),Time.fixedTimeAsDouble);}
}
