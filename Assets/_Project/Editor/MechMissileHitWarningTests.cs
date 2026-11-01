using NUnit.Framework;
using UnityEngine;
using System.Reflection;
using ACunicombat.Mechs;
public class MechMissileHitWarningTests
{
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
 GameObject host;MechImpactAudioController audio;
 [SetUp] public void Setup(){host=new GameObject("Hit warning test");audio=host.AddComponent<MechImpactAudioController>();typeof(MechImpactAudioController).GetMethod("Awake",Flags).Invoke(audio,null);}
 [TearDown] public void Cleanup(){Object.DestroyImmediate(host);}
 int Remaining=>(int)typeof(MechImpactAudioController).GetField("missileHitWarningRemaining",Flags).GetValue(audio);
 double Next=>(double)typeof(MechImpactAudioController).GetField("nextMissileHitWarningTime",Flags).GetValue(audio);
 void Tick(double t)=>typeof(MechImpactAudioController).GetMethod("TickMissileHitWarning",Flags).Invoke(audio,new object[]{t});
 [Test] public void OwnMissileHitPlaysExactlyThreeSeparatedPulses()
 {audio.PlayArmorImpact(TestWeaponKind.Missile,100,Vector3.zero,true);Assert.AreEqual(2,Remaining);Tick(Next-.001);Assert.AreEqual(2,Remaining);Tick(Next);Assert.AreEqual(1,Remaining);Tick(Next);Assert.AreEqual(0,Remaining);Tick(Next+100);Assert.AreEqual(0,Remaining);
 var source=(AudioSource)typeof(MechImpactAudioController).GetField("missileHitWarningSource",Flags).GetValue(audio);Assert.AreEqual(0,source.spatialBlend);Assert.IsFalse(source.loop);Assert.That(source.clip.length,Is.EqualTo(1.5285f).Within(.02));}
 [TestCase(TestWeaponKind.Rifle,true,100)] [TestCase(TestWeaponKind.Cannon,true,100)] [TestCase(TestWeaponKind.Missile,false,100)] [TestCase(TestWeaponKind.Missile,true,0)]
 public void UnrelatedHitsDoNotTrigger(TestWeaponKind kind,bool own,float damage){audio.PlayArmorImpact(kind,damage,Vector3.zero,own);Assert.AreEqual(0,Remaining);}
 [Test] public void RepeatedHitRefreshesBoundedBurstAndDisableCancels()
 {audio.PlayArmorImpact(TestWeaponKind.Missile,100,Vector3.zero,true);Tick(Next);Assert.AreEqual(1,Remaining);audio.PlayArmorImpact(TestWeaponKind.Missile,100,Vector3.zero,true);Assert.AreEqual(2,Remaining);typeof(MechImpactAudioController).GetMethod("OnDisable",Flags).Invoke(audio,null);Assert.AreEqual(0,Remaining);}
}
