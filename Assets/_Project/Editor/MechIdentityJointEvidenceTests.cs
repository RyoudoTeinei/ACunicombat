using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
public class MechIdentityJointEvidenceTests
{
 static TrackId Id(int n=1)=>new TrackId(new Handle(n,1));
 static void Put(MechIdentityEvidence m,IdentityEvidenceKind kind,double at,float q,float a=1,string key=null,int id=1)
 {m.Submit(Id(id),new IdentityEvidence{OriginalId=key??kind.ToString(),Kind=kind,Quality=q,Association=a,ObservedAt=at,ExpiresAt=at+15},at);}
 static void Lock(MechIdentityEvidence m,double at){Put(m,IdentityEvidenceKind.FireControlIllumination,at,.75f,.9f);m.Evaluate(Id(),at);}
 [TestCase(.98f,.8f,true)] [TestCase(.94f,.8f,false)] [TestCase(.98f,.79f,false)]
 public void ClearAttackSeparatesBehaviorCertaintyFromSourceAssociation(float q,float a,bool hostile)
 {var m=new MechIdentityEvidence();Put(m,IdentityEvidenceKind.ObservedAttack,0,q,a);var s=m.Evaluate(Id(),0);Assert.AreEqual(hostile,s.Relation==IdentityRelation.Hostile);Assert.AreEqual(hostile,s.CanInterceptThreat);}
 [Test] public void SustainedLockAndRecentLaunchConfirmAndLatchHostile()
 {var m=new MechIdentityEvidence();for(int i=0;i<=10;i++)Lock(m,i/5d);Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),2).Relation);
 Put(m,IdentityEvidenceKind.WeaponLaunch,2,.75f,.9f);var s=m.Evaluate(Id(),2);Assert.AreEqual(IdentityRelation.Hostile,s.Relation);Assert.AreEqual(IdentityReason.CombinedBehavior,s.Reason);Assert.IsTrue(s.CanAttackPlatform);Assert.IsFalse(s.UnderAttack);
 Put(m,IdentityEvidenceKind.FriendlyAuthentication,3,1);Assert.AreEqual(IdentityRelation.Hostile,m.Evaluate(Id(),3).Relation);Assert.AreEqual(IdentityRelation.Hostile,m.Evaluate(Id(),100).Relation);}
 [Test] public void OneRetainedLockPacketAndRepeatedLaunchCopiesCannotAccumulateDuration()
 {var m=new MechIdentityEvidence();Lock(m,0);for(int i=0;i<100;i++)Put(m,IdentityEvidenceKind.WeaponLaunch,0,.75f,.9f,"same-event");
 for(int i=1;i<=20;i++)Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),i*.2).Relation);}
 [TestCase(true)] [TestCase(false)] public void LaunchAloneOrLockAndJammingCannotConfirm(bool launch)
 {var m=new MechIdentityEvidence();for(int i=0;i<=20;i++){double t=i*.2;Put(m,launch?IdentityEvidenceKind.WeaponLaunch:IdentityEvidenceKind.DirectedJamming,t,.8f,.9f);if(!launch)Lock(m,t);Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),t).Relation);}}
 [Test] public void InterruptedLockMustBuildDurationAgain()
 {var m=new MechIdentityEvidence();Put(m,IdentityEvidenceKind.WeaponLaunch,0,.75f,.9f);Lock(m,0);Lock(m,.2);Lock(m,2);Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),2).Relation);}
 [TestCase(true)] [TestCase(false)] public void StaleOrOtherTrackLaunchCannotCombine(bool stale)
 {var m=new MechIdentityEvidence();Put(m,IdentityEvidenceKind.WeaponLaunch,0,.75f,.9f,id:stale?1:2);for(int i=0;i<=10;i++)Lock(m,6+i*.2);Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),8).Relation);}
 [Test] public void FixedFriendlySurvivesBothNewRulesAndExpiry()
 {var m=new MechIdentityEvidence();Put(m,IdentityEvidenceKind.FriendlyAuthentication,0,1);m.Evaluate(Id(),0);
 for(int i=1;i<=11;i++)Lock(m,i/5d);Put(m,IdentityEvidenceKind.WeaponLaunch,2.2,.75f,.9f);Put(m,IdentityEvidenceKind.ObservedAttack,2.2,.98f,.8f);
 var s=m.Evaluate(Id(),2.2);Assert.AreEqual(IdentityRelation.Friendly,s.Relation);Assert.IsTrue(s.Conflict);Assert.IsFalse(s.CanAttackPlatform);Assert.IsTrue(s.CanInterceptThreat);Assert.AreEqual(IdentityRelation.Friendly,m.Evaluate(Id(),100).Relation);}
 [Test] public void ExplicitCorrectionCannotReconsumeOldJointEvidence()
 {var m=new MechIdentityEvidence();for(int i=0;i<=10;i++)Lock(m,i/5d);Put(m,IdentityEvidenceKind.WeaponLaunch,2,.75f,.9f);Assert.AreEqual(IdentityRelation.Hostile,m.Evaluate(Id(),2).Relation);
 m.CorrectHostility(Id(),2);Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),2.1).Relation);for(int i=1;i<=12;i++)Lock(m,2+i*.2);Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),4.4).Relation);}

 [Test] public void FreshIlluminationEscalatesImmediatelyThenAtTwoAndFiveSeconds()
 {
  var m=new MechIdentityEvidence();Lock(m,0);var initial=m.Evaluate(Id(),0);
  Assert.AreEqual(IdentityRelation.SuspectedHostile,initial.Relation);Assert.AreEqual(2,MechBattlefieldMap.IdentitySuspicionLevel(initial));
  for(int i=1;i<=10;i++)Lock(m,i/5d);
  var high=m.Evaluate(Id(),2);Assert.AreEqual(IdentityRelation.SuspectedHostile,high.Relation);Assert.AreEqual(3,MechBattlefieldMap.IdentitySuspicionLevel(high));Assert.IsFalse(high.CanAttackPlatform);
  for(int i=11;i<25;i++)Lock(m,i/5d);
  Assert.AreNotEqual(IdentityRelation.Hostile,m.Evaluate(Id(),4.8).Relation);
  Lock(m,5);Assert.AreEqual(IdentityRelation.Hostile,m.Evaluate(Id(),5).Relation);Assert.AreEqual(IdentityReason.SustainedIllumination,m.Evaluate(Id(),5).Reason);
  Assert.AreEqual(IdentityRelation.Hostile,m.Evaluate(Id(),100).Relation);
 }
 [Test] public void SustainedIlluminationCannotFlipFixedFriendly()
 {var m=new MechIdentityEvidence();Put(m,IdentityEvidenceKind.FriendlyAuthentication,0,1);m.Evaluate(Id(),0);for(int i=1;i<=60;i++)Lock(m,i/5d);Assert.AreEqual(IdentityRelation.Friendly,m.Evaluate(Id(),12).Relation);}
 [TestCase(1,1)] [TestCase(2,1.15f)] [TestCase(8,2.05f)]
 public void TrailLengthsDashesAndWorldStrokeScaleMatchMarkerGrowth(float zoom,float expected)
 {Assert.That(MechBattlefieldMap.MapAnnotationScale(zoom)*zoom,Is.EqualTo(expected).Within(.0001));}
}
