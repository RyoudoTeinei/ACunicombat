using System;
using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
public sealed class MechIffIdentityTests
{
 [Test] public void ManualMarkOverridesRelationButDoesNotAuthenticateOrLeakAcrossTracks()
 {
  var model=new MechIdentityEvidence();var id=Id();
  model.Submit(id,Evidence("attack",IdentityEvidenceKind.ObservedAttack),0);
  Assert.That(model.SetManualRelation(id,IdentityRelation.Neutral,1),Is.True);
  var state=model.Evaluate(id,1);
  Assert.That(state.Relation,Is.EqualTo(IdentityRelation.Neutral));Assert.That(state.CanAttackPlatform,Is.False);
  Assert.That(state.Iff,Is.EqualTo(IffOutcome.None));Assert.That(state.Conflict,Is.True);Assert.That(model.HasDecidedIdentity(id),Is.True);
  model.SetManualRelation(id,IdentityRelation.Friendly,2);
  Assert.That(model.Evaluate(id,2).Relation,Is.EqualTo(IdentityRelation.Friendly));Assert.That(model.HasDecidedIdentity(id),Is.True);
  model.SetManualRelation(id,IdentityRelation.Hostile,3);
  Assert.That(model.Evaluate(id,3).CanAttackPlatform,Is.True);Assert.That(model.HasDecidedIdentity(id),Is.True);
  Assert.That(model.Evaluate(Id(2),3).Relation,Is.EqualTo(IdentityRelation.Unknown));
  model.Forget(id);Assert.That(model.Evaluate(id,4).Relation,Is.EqualTo(IdentityRelation.Unknown));
 }
 [Test] public void UnknownAndNeutralRemainEligibleForRoutineIdentification()
 {
  var model=new MechIdentityEvidence();var id=Id();Assert.That(model.HasDecidedIdentity(id),Is.False);
  model.SetManualRelation(id,IdentityRelation.Neutral,0);model.Evaluate(id,0);Assert.That(model.HasDecidedIdentity(id),Is.False);
 }
 [Test] public void ManualMarkSurvivesIffUpdateWithoutChangingAuthenticationOutcome()
 {
  var model=new MechIdentityEvidence();var id=Id();model.SetManualRelation(id,IdentityRelation.Hostile,0);
  model.SetIff(id,IffOutcome.Authenticated);model.Submit(id,Evidence("iff",IdentityEvidenceKind.FriendlyAuthentication),1);
  var state=model.Evaluate(id,2);Assert.That(state.Relation,Is.EqualTo(IdentityRelation.Hostile));Assert.That(state.Iff,Is.EqualTo(IffOutcome.Authenticated));
  Assert.That(model.SetManualRelation(default,IdentityRelation.Friendly,3),Is.False);
 }
 static TrackId Id(int index=1,int generation=1)=>new TrackId(new Handle(index,(byte)generation));
 static IffCredential Credential(string domain="blue")=>new IffCredential(Guid.NewGuid(),domain,10000,IffAuthority.NewSecret());
 static IdentityEvidence Evidence(string id,IdentityEvidenceKind kind,double at=0,double until=100,float quality=1)=>new IdentityEvidence{OriginalId=id,Source="test",Kind=kind,ObservedAt=at,ExpiresAt=until,Quality=quality,Association=1};
 [Test] public void GenuineChallengeAndResponseAuthenticate()
 {
  var a=new IffAuthority();var sender=Credential();var target=Credential();a.Register(sender);a.Register(target);
  var q=a.Challenge(sender,1,2);Assert.That(a.VerifyChallenge(q,1.1),Is.True);
  var r=a.Reply(target,q,1.2,new double3(1000,0,0),double3.zero);
  Assert.That(a.VerifyReply(r,q,sender.Subject,1.3),Is.EqualTo(IffOutcome.Authenticated));
 }
 [Test] public void TamperingIdentityOrNavigationInvalidatesSignature()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var q=a.Challenge(c,0,2);var r=a.Reply(c,q,.1,double3.zero,double3.zero);r.Position.x=500;
  Assert.That(a.VerifyReply(r,q,c.Subject,.2),Is.EqualTo(IffOutcome.InvalidProof));
 }
 [Test] public void ReplayedChallengeAndWrongRequesterAreRejected()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var q=a.Challenge(c,0,2);var r=a.Reply(c,q,.1,double3.zero,double3.zero);
  Assert.That(a.VerifyReply(r,a.Challenge(c,0,2),c.Subject,.2),Is.EqualTo(IffOutcome.Replay));
  Assert.That(a.VerifyReply(r,q,Guid.NewGuid(),.2),Is.EqualTo(IffOutcome.Replay));
 }
 [Test] public void ExpiredRevokedUntrustedAndCorruptAreDifferentOutcomes()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var q=a.Challenge(c,0,2);var r=a.Reply(c,q,.1,double3.zero,double3.zero);
  Assert.That(a.VerifyReply(r,q,c.Subject,3),Is.EqualTo(IffOutcome.Expired));
  c.Revoked=true;Assert.That(a.VerifyReply(r,q,c.Subject,.2),Is.EqualTo(IffOutcome.InvalidProof));
  c.Revoked=false;a.Remove(c.Subject);Assert.That(a.VerifyReply(r,q,c.Subject,.2),Is.EqualTo(IffOutcome.Untrusted));
  r.Proof=null;Assert.That(a.VerifyReply(r,q,c.Subject,.2),Is.EqualTo(IffOutcome.Corrupt));
 }
 [Test] public void LongRangeTimeoutIncludesLightTravelAndProcessing()
 {Assert.That(MechIffProtocol.TimeoutSeconds(299792458,0,.25),Is.EqualTo(2.5).Within(.0001));Assert.That(MechIffRadio.TravelSeconds(299792458),Is.EqualTo(1));}
 [Test] public void LinkBudgetFallsWithRangeAndInterference()
 {double a=MechIffRadio.LinkMargin(1000,1e9,40,32,-115,0);double b=MechIffRadio.LinkMargin(2000,1e9,40,32,-115,0);Assert.That(a-b,Is.EqualTo(6.0206).Within(.001));Assert.That(MechIffRadio.LinkMargin(1000,1e9,40,32,-115,20),Is.EqualTo(a-20));}
 [Test] public void DuplicateRequestsCapacityTimeoutAndCooldownAreBounded()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var p=new MechIffProtocol(a,c){Capacity=1};IffOutcome result=IffOutcome.None;p.Result+=(id,o,r)=>result=o;
  var q=p.Begin(Id(),1000,0);Assert.That(q,Is.Not.Null);Assert.That(p.Begin(Id(),1000,0),Is.Null);
  Assert.That(p.Begin(Id(2),1000,0),Is.Null);Assert.That(result,Is.EqualTo(IffOutcome.CapacityLimited));
  p.Tick(q.Deadline+.01);Assert.That(result,Is.EqualTo(IffOutcome.NoResponse));Assert.That(p.Begin(Id(),1000,1),Is.Null);Assert.That(p.Begin(Id(),1000,6),Is.Not.Null);
 }
 [Test] public void ProtocolRejectsDuplicateAndLateReplies()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var p=new MechIffProtocol(a,c);var q=p.Begin(Id(),1000,0);var r=a.Reply(c,q,.1,double3.zero,double3.zero);
  Assert.That(p.Receive(r,.2),Is.EqualTo(IffOutcome.Authenticated));Assert.That(p.Receive(r,.21),Is.EqualTo(IffOutcome.Replay));p.Tick(1);Assert.That(p.Receive(r,1),Is.EqualTo(IffOutcome.Replay));
 }
 [Test] public void IlluminationAssociationMatchesRwrBearingTolerance()
 {
  Assert.That(MechIdentityController.IlluminationAssociationHalfAngleDegrees,Is.EqualTo(12).Within(.001));
  Assert.That(MechIdentityController.IlluminationAssociationHalfAngleDegrees,Is.GreaterThanOrEqualTo(8));
 }
 [Test] public void NoReplyNeverMakesAnUnknownTargetHostile()
 {var f=new MechIdentityEvidence();f.SetIff(Id(),IffOutcome.NoResponse);var s=f.Evaluate(Id(),1);Assert.That(s.Relation,Is.EqualTo(IdentityRelation.Unknown));Assert.That(s.CanAttackPlatform,Is.False);}
 [Test] public void ForwardedCopiesCannotAccumulateWeakSuspicion()
 {
  var f=new MechIdentityEvidence();for(int i=0;i<100;i++)f.Submit(Id(),Evidence("original",IdentityEvidenceKind.FireControlIllumination,0,100,.4f),0);
  f.Evaluate(Id(),0);Assert.That(f.Evaluate(Id(),10).Relation,Is.EqualTo(IdentityRelation.Unknown));
 }
 [Test] public void SuspectedHostileRequiresHoldAndExitsWithHysteresis()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("lock",IdentityEvidenceKind.FireControlIllumination,0,4),0);
  var initial=f.Evaluate(Id(),0);Assert.That(initial.Relation,Is.EqualTo(IdentityRelation.SuspectedHostile));Assert.That(initial.Suspicion,Is.EqualTo(.7f).Within(.001));Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(initial),Is.EqualTo(2));
  var suspected=f.Evaluate(Id(),2);Assert.That(suspected.Relation,Is.EqualTo(IdentityRelation.SuspectedHostile));Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(suspected),Is.EqualTo(2));
  var held=f.Evaluate(Id(),4);Assert.That(held.Relation,Is.EqualTo(IdentityRelation.SuspectedHostile));Assert.That(held.Suspicion,Is.Zero);Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(held),Is.EqualTo(2));
  var cleared=f.Evaluate(Id(),10);Assert.That(cleared.Relation,Is.EqualTo(IdentityRelation.Unknown));Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(cleared),Is.Zero);
 }
 [Test] public void SuspicionMarkerHasDiscreteLevelsAndDoesNotGrantAttackPermission()
 {
  var identity=new ContactIdentitySnapshot{Available=true,Party=IdentityParty.ThirdParty,Relation=IdentityRelation.Unknown,Suspicion=.3f,CanAttackPlatform=false};
  Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(identity),Is.EqualTo(1));Assert.That(identity.CanAttackPlatform,Is.False);
  identity.Suspicion=.7f;Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(identity),Is.EqualTo(1));
  identity.Relation=IdentityRelation.SuspectedHostile;Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(identity),Is.EqualTo(2));
  identity.Suspicion=.9f;Assert.That(MechBattlefieldMap.IdentitySuspicionLevel(identity),Is.EqualTo(3));Assert.That(identity.CanAttackPlatform,Is.False);
 }
 [Test] public void NeutralAndThirdPartyDoNotBecomeHostileOnSilence()
 {
  var f=new MechIdentityEvidence{AllowUnknownPlatformAttack=true};f.Submit(Id(),Evidence("neutral",IdentityEvidenceKind.NeutralAuthentication),0);f.SetIff(Id(),IffOutcome.NoResponse);
  var n=f.Evaluate(Id(),1);Assert.That(n.Relation,Is.EqualTo(IdentityRelation.Neutral));Assert.That(n.Party,Is.EqualTo(IdentityParty.ThirdParty));Assert.That(n.CanAttackPlatform,Is.False);
  f.Submit(Id(2),Evidence("third",IdentityEvidenceKind.ThirdPartyAuthentication),1);var t=f.Evaluate(Id(2),1);Assert.That(t.Party,Is.EqualTo(IdentityParty.ThirdParty));Assert.That(t.CanAttackPlatform,Is.False);
 }
 [Test] public void ConfirmedFriendlyAttackKeepsStableIdentityAndDefensePermission()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("auth",IdentityEvidenceKind.FriendlyAuthentication),0);f.Submit(Id(),Evidence("attack",IdentityEvidenceKind.ObservedAttack),0);
  var s=f.Evaluate(Id(),1);Assert.That(s.Relation,Is.EqualTo(IdentityRelation.Friendly));Assert.That(s.Conflict,Is.True);Assert.That(s.CanInterceptThreat,Is.True);Assert.That(s.CanAttackPlatform,Is.False);
  Assert.That(f.HasDecidedIdentity(Id()),Is.True);Assert.That(f.Evaluate(Id(),101).Relation,Is.EqualTo(IdentityRelation.Friendly));
 }
 [Test] public void ConfirmedAttackPersistsUntilExplicitCorrection()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("attack",IdentityEvidenceKind.ObservedAttack,0,2),0);f.Evaluate(Id(),0);
  Assert.That(f.Evaluate(Id(),10).Relation,Is.EqualTo(IdentityRelation.Hostile));f.CorrectHostility(Id());Assert.That(f.Evaluate(Id(),11).Relation,Is.EqualTo(IdentityRelation.Unknown));
 }
 [Test] public void ConfirmedHostileIgnoresLaterFriendlyEvidence()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("attack",IdentityEvidenceKind.ObservedAttack),0);f.Evaluate(Id(),0);
  f.Submit(Id(),Evidence("auth",IdentityEvidenceKind.FriendlyAuthentication,1),1);var s=f.Evaluate(Id(),1);
  Assert.That(s.Relation,Is.EqualTo(IdentityRelation.Hostile));Assert.That(s.Conflict,Is.True);Assert.That(s.CanAttackPlatform,Is.True);Assert.That(f.HasDecidedIdentity(Id()),Is.True);
 }
 [Test] public void DecidedFriendlySurvivesExpiryAndNeverTransfersToReusedHandleGeneration()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("auth",IdentityEvidenceKind.FriendlyAuthentication,0,5),0);
  Assert.That(f.Evaluate(Id(),1).Relation,Is.EqualTo(IdentityRelation.Friendly));Assert.That(f.Evaluate(Id(1,2),1).Relation,Is.EqualTo(IdentityRelation.Unknown));
  var expired=f.Evaluate(Id(),6);Assert.That(expired.Relation,Is.EqualTo(IdentityRelation.Friendly));Assert.That(expired.Confidence,Is.EqualTo(1));Assert.That(expired.ValidUntil,Is.EqualTo(double.MaxValue));
 }
 [Test] public void OpticalAndWaveformIdentityRemainDisabled()
 {var f=new MechIdentityEvidence();Assert.That(f.Submit(Id(),Evidence("optical",IdentityEvidenceKind.OpticalIdentity),0),Is.False);Assert.That(f.Submit(Id(),Evidence("rf",IdentityEvidenceKind.WaveformIdentity),0),Is.False);}
 [Test] public void ImpactBearingAloneDoesNotConfirmHostilePlatform()
 {var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("bearing",IdentityEvidenceKind.IncomingFireBearing),0);f.Evaluate(Id(),0);Assert.That(f.Evaluate(Id(),3).Relation,Is.EqualTo(IdentityRelation.SuspectedHostile));}
 [Test] public void TrainingUnknownPermissionIsExplicit()
 {var f=new MechIdentityEvidence();Assert.That(f.Evaluate(Id(),0).CanAttackPlatform,Is.False);f.AllowUnknownPlatformAttack=true;Assert.That(f.Evaluate(Id(),1).CanAttackPlatform,Is.True);}
 static TrackSnapshot Track(int id,double x){var t=new TrackSnapshot{TrackId=Id(id),Position=new double3(x,0,0),LastUpdateTime=1,LastPredictTime=1,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle};for(int j=0;j<6;j++)t.SetCovariance(j,j,100);return t;}
 [Test] public void ValidResponseDoesNotAuthenticateTwoNearbyCandidates()
 {
  var reply=new IffReply{Position=new double3(1000,0,0),Velocity=double3.zero,Sent=1};
  Assert.That(MechIdentityController.AssociateReply(new[]{Track(1,1000),Track(2,1001)},2,reply,Vector3.zero,Vector3.right,1,1),Is.EqualTo(-1));
  Assert.That(MechIdentityController.AssociateReply(new[]{Track(1,1000),Track(2,2000)},2,reply,Vector3.zero,Vector3.right,1,1),Is.EqualTo(0));
  Assert.That(MechIdentityController.AssociateReply(new[]{Track(1,1000)},1,reply,Vector3.zero,Vector3.left,1,1),Is.EqualTo(-1));
 }
 [Test] public void RepeatedUnattributedImpactsRemainSuspiciousEvenWithIllumination()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("hit1",IdentityEvidenceKind.IncomingFireBearing,0,20),0);f.Submit(Id(),Evidence("hit2",IdentityEvidenceKind.IncomingFireBearing,1,20),1);
  Assert.That(f.Evaluate(Id(),1).Relation,Is.Not.EqualTo(IdentityRelation.Hostile));
  f.Submit(Id(),Evidence("lock",IdentityEvidenceKind.FireControlIllumination,1,20),1);Assert.That(f.Evaluate(Id(),2).Relation,Is.Not.EqualTo(IdentityRelation.Hostile));
 }
 [Test] public void FriendlyLatchRequiresExplicitRevocationAfterEvidenceRemoval()
 {
  var f=new MechIdentityEvidence();f.Submit(Id(),Evidence("iff",IdentityEvidenceKind.FriendlyAuthentication),0);f.Submit(Id(),Evidence("link",IdentityEvidenceKind.CooperativeFriendly),0);
  f.RemoveEvidence(Id(),"iff");Assert.That(f.Evaluate(Id(),1).Relation,Is.EqualTo(IdentityRelation.Friendly));
  f.RemoveEvidence(Id(),"link");Assert.That(f.Evaluate(Id(),2).Relation,Is.EqualTo(IdentityRelation.Friendly));
  f.RevokeFriendlyIdentity(Id());Assert.That(f.HasDecidedIdentity(Id()),Is.False);Assert.That(f.Evaluate(Id(),3).Relation,Is.EqualTo(IdentityRelation.Unknown));
 }
 [Test] public void InvalidReplyIsNotOverwrittenAsRadioSilence()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var p=new MechIffProtocol(a,c);IffOutcome result=IffOutcome.None;p.Result+=(id,o,r)=>result=o;
  var q=p.Begin(Id(),1000,0);var r=a.Reply(c,q,.1,double3.zero,double3.zero);r.Proof[0]^=1;
  p.Receive(r,.2);p.Tick(1);Assert.That(result,Is.EqualTo(IffOutcome.InvalidProof));
 }
 [Test] public void MissingTrackCancelsTransactionAndOldReplyCannotAttachToNewGeneration()
 {
  var a=new IffAuthority();var c=Credential();a.Register(c);var p=new MechIffProtocol(a,c);var q=p.Begin(Id(),1000,0);var r=a.Reply(c,q,.1,double3.zero,double3.zero);
  p.CancelMissing(new System.Collections.Generic.HashSet<TrackId>{Id(1,2)},.2);Assert.That(p.PendingCount,Is.Zero);Assert.That(p.Receive(r,.2),Is.EqualTo(IffOutcome.Replay));
 }
 [Test] public void ReplyForAnotherTrackDoesNotAuthenticateTheAskedTrack()
 {
  var go=new GameObject("IFF association publication");go.SetActive(false);
  try
  {
   var radar=go.AddComponent<MechRadarController>();var controller=go.AddComponent<MechIdentityController>();controller.LogDecisions=false;
   const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
   typeof(MechIdentityController).GetField("<Radar>k__BackingField",flags).SetValue(controller,radar);
   typeof(MechIdentityController).GetField("<Credential>k__BackingField",flags).SetValue(controller,Credential());
   var tracks=(TrackSnapshot[])typeof(MechIdentityController).GetField("tracks",flags).GetValue(controller);
   tracks[0]=Track(1,1000);tracks[1]=Track(2,2000);for(int i=0;i<2;i++){tracks[i].LastUpdateTime=0;tracks[i].LastPredictTime=0;}
   typeof(MechIdentityController).GetField("count",flags).SetValue(controller,2);
   typeof(MechIdentityController).GetField("pendingBearing",flags).SetValue(controller,Vector3.right);
   var reply=new IffReply{Subject=Guid.NewGuid(),Domain="blue",Position=new double3(2000,0,0),Sent=Time.timeAsDouble,Expires=10000};
   typeof(MechIdentityController).GetMethod("OnIffResult",flags).Invoke(controller,new object[]{Id(1),IffOutcome.Authenticated,reply});
   var reducer=(MechIdentityEvidence)typeof(MechIdentityController).GetField("identities",flags).GetValue(controller);
   Assert.That(reducer.Evaluate(Id(1),Time.timeAsDouble).Iff,Is.EqualTo(IffOutcome.AuthenticatedUnassociated));
   Assert.That(reducer.Evaluate(Id(1),Time.timeAsDouble).Relation,Is.EqualTo(IdentityRelation.Unknown));
   Assert.That(reducer.Evaluate(Id(2),Time.timeAsDouble).Relation,Is.EqualTo(IdentityRelation.Friendly));
  }
  finally{UnityEngine.Object.DestroyImmediate(go);}
 }
 [TestCase(IdentityEvidenceKind.FriendlyAuthentication)] [TestCase(IdentityEvidenceKind.NeutralAuthentication)] [TestCase(IdentityEvidenceKind.ThirdPartyAuthentication)]
 public void ExpiredProtectedIdentityCannotBecomeTrainingFreeFireTarget(IdentityEvidenceKind kind)
 {
  var f=new MechIdentityEvidence{AllowUnknownPlatformAttack=true};f.Submit(Id(),Evidence("identity",kind,0,2),0);f.Evaluate(Id(),1);
  var stale=f.Evaluate(Id(),3);bool friendly=kind==IdentityEvidenceKind.FriendlyAuthentication;
  Assert.That(stale.Relation,Is.EqualTo(friendly?IdentityRelation.Friendly:IdentityRelation.Unknown));Assert.That(stale.CanAttackPlatform,Is.False);Assert.That(stale.Reason,Is.EqualTo(friendly?IdentityReason.Authenticated:IdentityReason.EvidenceExpired));Assert.That(f.HasDecidedIdentity(Id()),Is.EqualTo(friendly));
 }
}
