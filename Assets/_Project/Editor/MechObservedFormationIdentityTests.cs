using System.Collections.Generic;
using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
public class MechObservedFormationIdentityTests
{
 static TrackId Id(int i)=>new TrackId(new Handle(i,1));
 static TrackSnapshot Track(int i,double3 p,double3 v,double t){var x=new TrackSnapshot{TrackId=Id(i),Position=p,Velocity=v,LastUpdateTime=t,LastPredictTime=t,Confidence=.95f,ObjectClass=TrackObjectClass.AirVehicle,Lifecycle=TrackLifecycle.Confirmed};for(int a=0;a<6;a++)x.SetCovariance(a,a,25);return x;}
 static Dictionary<TrackId,ContactIdentitySnapshot> Identities(IdentityRelation member=IdentityRelation.Unknown,IdentityReason seed=IdentityReason.ObservedAttack)=>new Dictionary<TrackId,ContactIdentitySnapshot>{{Id(1),new ContactIdentitySnapshot{Available=true,Relation=IdentityRelation.Hostile,Reason=seed}},{Id(2),new ContactIdentitySnapshot{Available=true,Relation=member}}};
 [Test] public void StableObservedFormationPropagatesAfterThreeSeconds()
 {var f=new MechObservedFormationIdentity();var ids=Identities();var output=new List<MechObservedFormationIdentity.Inference>();for(int i=0;i<=6;i++){double t=i*.5;f.Update(new[]{Track(1,new double3(t*200,0,0),new double3(200,0,0),t),Track(2,new double3(t*200,500,0),new double3(200,0,0),t)},2,ids,t,output);Assert.AreEqual(i==6?1:0,output.Count);}Assert.AreEqual(Id(2),output[0].Member);Assert.AreEqual(Id(1),output[0].HostileAnchor);}
 [TestCase(IdentityRelation.Friendly)] [TestCase(IdentityRelation.Hostile)] [TestCase(IdentityRelation.Neutral)]
 public void DecidedOrNeutralIdentityIsNotOverwritten(IdentityRelation relation)
 {RunNegative(relation,IdentityReason.ObservedAttack,500,new double3(200,0,0),false,false);}
 [Test] public void InferredHostileCannotCauseCascade(){RunNegative(IdentityRelation.Unknown,IdentityReason.FormationAssociation,500,new double3(200,0,0),false,false);}
 [Test] public void NearbyCrossingTrafficDoesNotQualify(){RunNegative(IdentityRelation.Unknown,IdentityReason.ObservedAttack,500,new double3(0,200,0),false,false);}
 [Test] public void DistantParallelTrafficDoesNotQualify(){RunNegative(IdentityRelation.Unknown,IdentityReason.ObservedAttack,6000,new double3(200,0,0),false,false);}
 [Test] public void OldOrImpreciseTracksDoNotQualify(){RunNegative(IdentityRelation.Unknown,IdentityReason.ObservedAttack,500,new double3(200,0,0),true,false);RunNegative(IdentityRelation.Unknown,IdentityReason.ObservedAttack,500,new double3(200,0,0),false,true);}
 static void RunNegative(IdentityRelation r,IdentityReason reason,double distance,double3 velocity,bool old,bool imprecise)
 {var f=new MechObservedFormationIdentity();var ids=Identities(r,reason);var output=new List<MechObservedFormationIdentity.Inference>();for(int i=0;i<=10;i++){double t=i*.5;var member=Track(2,new double3(0,distance,0),velocity,old?t-2:t);if(imprecise)member.SetCovariance(0,0,1e6);f.Update(new[]{Track(1,double3.zero,new double3(200,0,0),t),member},2,ids,t,output);Assert.IsEmpty(output);}}
 [Test] public void InferenceLatchesOnlyUnknownAndDoesNotChangeFriendly()
 {var m=new MechIdentityEvidence();m.Submit(Id(1),new IdentityEvidence{OriginalId="iff",Kind=IdentityEvidenceKind.FriendlyAuthentication,Quality=1,Association=1,ObservedAt=0,ExpiresAt=10},0);m.Evaluate(Id(1),0);for(int i=1;i<=2;i++){m.Submit(Id(i),new IdentityEvidence{OriginalId="formation",Kind=IdentityEvidenceKind.FormationHostile,Quality=.95f,Association=1,ObservedAt=1,ExpiresAt=4},1);var s=m.Evaluate(Id(i),1);Assert.AreEqual(i==1?IdentityRelation.Friendly:IdentityRelation.Hostile,s.Relation);}Assert.AreEqual(IdentityReason.FormationAssociation,m.Evaluate(Id(2),100).Reason);}
}
