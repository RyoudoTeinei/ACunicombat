using System.Collections;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public class MechProjectileBirthEpochTests
{
 [UnityTest] public IEnumerator SpawnBetweenTicksDoesNotBackdateOrOverintegrate()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  float scale=Time.timeScale,capture=Time.captureDeltaTime,step=Time.fixedDeltaTime;Time.timeScale=1;Time.captureDeltaTime=1f/30;Time.fixedDeltaTime=.02f;
  GameObject go=null;
  try{
   int frames=0;while(Time.timeAsDouble-Time.fixedTimeAsDouble<.005&&frames++<30)yield return null;
   Assert.Greater(Time.timeAsDouble-Time.fixedTimeAsDouble,.005);
   var origin=new Vector3(100,300,500);var velocity=new Vector3(1400,800,1200);double birth=Time.timeAsDouble;
   go=new GameObject("Between-tick projectile");go.transform.position=origin;var projectile=go.AddComponent<MechTestProjectile>();projectile.Configure(null,null,default,TestWeaponKind.Rifle,velocity,1,.01f,10);
   Assert.AreEqual(birth,projectile.PhysicsStateTime,1e-9);Assert.AreEqual(origin,projectile.PositionAtPhysicsTime(birth));Assert.AreEqual(origin,projectile.PositionAtPhysicsTime(birth-.02));
   for(int i=0;i<10;i++){
    yield return null;var expected=origin+velocity*(float)(projectile.PhysicsStateTime-birth);
    Assert.Less(Vector3.Distance(expected,go.transform.position),.004f,"Do not add a whole pre-birth physics interval");
    Assert.Less(Vector3.Distance(origin+velocity*(float)(Time.timeAsDouble-birth),projectile.PositionAtPhysicsTime(Time.timeAsDouble)),.004f);
   }
  }finally{if(go!=null)Object.DestroyImmediate(go);Time.timeScale=scale;Time.captureDeltaTime=capture;Time.fixedDeltaTime=step;}
  yield return new ExitPlayMode();
 }
}
