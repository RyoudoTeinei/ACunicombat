using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public sealed class MechApsCollisionReplayTests
{
    private readonly List<GameObject> objects=new();
    private GameObject Create(string name){var go=new GameObject(name);objects.Add(go);return go;}
    [TearDown] public void Cleanup(){foreach(var go in objects)if(go!=null)Object.DestroyImmediate(go);objects.Clear();}
    [UnityTest] public IEnumerator StaleColliderPoseCannotPutContactTargetBehindBlast()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        bool previous=Physics.autoSyncTransforms;
        try
        {
            Physics.autoSyncTransforms=false;
            var enemy=Create("Hostile");var body=Create("Moving missile");body.transform.position=Vector3.forward*90;
            body.AddComponent<SphereCollider>().radius=.1f;Physics.SyncTransforms();
            body.transform.position=Vector3.forward*70;
            var missile=body.AddComponent<MechTestProjectile>();missile.Configure(enemy.transform,null,default,TestWeaponKind.Missile,Vector3.back*1000,10,.1f,8,newSeekerActivationDelay:20);
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            double tickStart=Time.fixedTimeAsDouble-.02;
            typeof(MechTestProjectile).GetField("previousPhysicsPosition",flags).SetValue(missile,Vector3.forward*90);
            typeof(MechTestProjectile).GetField("previousPhysicsTime",flags).SetValue(missile,tickStart);
            typeof(MechTestProjectile).GetField("physicsStateTime",flags).SetValue(missile,Time.fixedTimeAsDouble);
            var owner=Create("Defender");var charge=Create("Countermunition");charge.transform.position=Vector3.forward*84.5f;
            Assert.That(Physics.Raycast(charge.transform.position,Vector3.forward,out var staleHit,18),Is.True);
            var oldDelta=missile.PositionAtPhysicsTime(tickStart+staleHit.distance/900)-staleHit.point;
            Assert.That(Vector3.Dot(oldDelta,Vector3.forward),Is.LessThan(-5),"Reproduce the logged behind-cone contradiction");
            var shot=charge.AddComponent<MechApsProjectile>();shot.Configure(owner.transform,Vector3.forward*900,1,8,160,Vector3.forward,60,35);shot.Step(.02f);
            Assert.That(shot.DamagedMissiles,Is.EqualTo(1),shot.BlastDiagnostic);Assert.That(missile.IsInterceptableMissile,Is.False);
            var delta=missile.PositionAtPhysicsTime(shot.BurstTimeSeconds)-charge.transform.position;
            Assert.That(Vector3.Dot(delta,Vector3.forward),Is.GreaterThan(0));Assert.That(delta.magnitude,Is.LessThan(.5));
            Assert.That(Vector3.Distance(charge.transform.position,owner.transform.position),Is.GreaterThanOrEqualTo(35));
        }
        finally{Physics.autoSyncTransforms=previous;Cleanup();}
        yield return new ExitPlayMode();
    }
}
