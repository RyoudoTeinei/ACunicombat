using System;
using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
public sealed class MechFleetTests
{
    Guid leader,wing,fleet,weapon;MechFleetState state;
    [SetUp] public void Setup(){leader=Guid.NewGuid();wing=Guid.NewGuid();fleet=Guid.NewGuid();weapon=Guid.NewGuid();state=new MechFleetState(wing,fleet,leader,false,false,FleetAttackMode.Confirmed);}
    FleetPacket Order(long rev=1,FleetOrderKind kind=FleetOrderKind.Attack)=>new FleetPacket{Kind=FleetPacketKind.Order,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=rev,Order=kind,Weapon=weapon,ObservedAt=1,ExpiresAt=4,TaskDeadline=30};
    [Test] public void ConfirmedRequiresExplicitWeaponButFreeDoesNot(){Assert.IsFalse(state.AllowsOffensiveMissile(weapon));var p=Order(kind:FleetOrderKind.AttackWithWeapon);Assert.IsTrue(state.Receive(p,1));Assert.IsTrue(state.AllowsOffensiveMissile(weapon));Assert.IsFalse(state.AllowsOffensiveMissile(Guid.NewGuid()));Assert.IsFalse(state.AllowsWeapon(Guid.NewGuid()));state.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.EngagementCompleted,2);Assert.IsFalse(state.AllowsOffensiveMissile(weapon));p=Order(2,FleetOrderKind.SetAttackMode);p.Mode=FleetAttackMode.Free;state.Receive(p,2);Assert.IsTrue(state.AllowsOffensiveMissile(weapon));}
    [Test] public void ExplicitPlayerAttackAllowsWeaponSelectionUntilReturn()
    {
        var p=Order();p.AuthorizedWeaponSelection=true;
        Assert.IsTrue(state.Receive(p,1));Assert.IsTrue(state.AllowsOffensiveMissile(weapon));
        Assert.IsTrue(state.AllowsOffensiveMissile(Guid.NewGuid()));
        state.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.Expired,2);
        Assert.IsFalse(state.AllowsOffensiveMissile(weapon));
    }
    [Test] public void GenericAttackDoesNotGrantOffensiveMissiles(){state.Receive(Order(),1);Assert.IsFalse(state.AllowsOffensiveMissile(weapon));}
    [Test] public void UnauthorizedSenderCannotOrder(){var p=Order();p.Sender=Guid.NewGuid();Assert.IsFalse(state.Receive(p,1));Assert.IsNull(state.Task);}
    [Test] public void PlayerNeverReceivesAiOrders(){state=new MechFleetState(wing,fleet,leader,true,true,FleetAttackMode.Confirmed);state.GrantCommand(leader);FleetPacket feedback=default;state.Feedback+=p=>feedback=p;Assert.IsFalse(state.Receive(Order(),1));Assert.AreEqual(FleetTaskReason.PlayerIsNotSubordinate,feedback.Reason);}
    [Test] public void IndependentLeaderRequiresExplicitGrant(){state=new MechFleetState(wing,fleet,wing,false,true,FleetAttackMode.Free);Assert.IsFalse(state.Receive(Order(),1));state.GrantCommand(leader);Assert.IsTrue(state.Receive(Order(),1));Assert.IsFalse(state.Task.Detached);}
    [Test] public void SingleWingDetachesAndOnlyCompletesOnReturn(){state.Receive(Order(),1);Assert.IsTrue(state.Task.Detached);state.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.EngagementCompleted,2);Assert.IsTrue(state.Task.Detached);state.SetPhase(FleetTaskPhase.Completed,FleetTaskReason.EngagementCompleted,3);Assert.IsFalse(state.Task.Detached);}
    [Test] public void WholeFleetOrderKeepsFormation(){var p=Order();p.WholeFleet=true;state.Receive(p,1);Assert.IsFalse(state.Task.Detached);}
    [Test] public void DuplicateDoesNotResetExecution(){var p=Order();state.Receive(p,1);state.SetPhase(FleetTaskPhase.Executing,FleetTaskReason.None,2);Assert.IsFalse(state.Receive(p,2));Assert.AreEqual(FleetTaskPhase.Executing,state.Task.Phase);}
    [Test] public void SameRevisionDifferentOperationRejected(){state.Receive(Order(),1);Assert.IsFalse(state.Receive(Order(),2));}
    [Test] public void ReorderedOldCommandCannotReplaceNew(){var p=Order(2);state.Receive(p,1);Assert.IsFalse(state.Receive(Order(1),2));Assert.AreEqual(p.Operation,state.Task.Order.Operation);}
    [Test] public void CancelAndDelayedOriginalDoNotRestartTask(){var p=Order();state.Receive(p,1);var c=p;c.Kind=FleetPacketKind.Cancel;c.Revision=2;state.Receive(c,2);Assert.IsFalse(state.Receive(p,2));Assert.AreEqual(FleetTaskPhase.Returning,state.Task.Phase);}
    [Test] public void ExpiredTransmissionNeverExecutes(){Assert.IsFalse(state.Receive(Order(),5));Assert.IsNull(state.Task);}
    [Test] public void TransportExpiryDoesNotExpireAcceptedTask(){state.Receive(Order(),1);state.Tick(5);Assert.AreEqual(FleetTaskPhase.Acquiring,state.Task.Phase);state.Tick(31);Assert.AreEqual(FleetTaskPhase.Returning,state.Task.Phase);Assert.AreEqual(FleetTaskReason.Expired,state.Task.Reason);}
    [Test] public void MovementInterfaceReportsUnsupported(){FleetPacket response=default;state.Feedback+=p=>response=p;Assert.IsFalse(state.Receive(Order(kind:FleetOrderKind.Move),1));Assert.AreEqual(FleetTaskReason.UnsupportedMovement,response.Reason);Assert.IsNull(state.Task);}
    FleetPacket Claim(Guid sender,Guid contact,float cost,bool committed=false)=>new FleetPacket{Kind=committed?FleetPacketKind.DefenseCommitted:FleetPacketKind.DefenseClaim,Sender=sender,Fleet=fleet,Contact=contact,ObservedAt=1,ExpiresAt=3,LeaseUntil=3,EstimatedSeconds=cost,Revision=1};
    [Test] public void CompetingDefendersConvergeRegardlessOfArrivalOrder(){var id=Guid.NewGuid();var a=Claim(leader,id,2);var b=Claim(wing,id,1);var other=new MechFleetState(Guid.NewGuid(),fleet,leader,false,false,FleetAttackMode.Confirmed);state.Receive(a,1);state.Receive(b,1);other.Receive(b,1);other.Receive(a,1);Assert.AreEqual(wing,state.Claims[id].Sender);Assert.AreEqual(wing,other.Claims[id].Sender);}
    [Test] public void InFlightCommitIsNotPreemptedByCheapBid(){var id=Guid.NewGuid();state.Receive(Claim(leader,id,2,true),1);state.Receive(Claim(wing,id,0),1);Assert.AreEqual(leader,state.Claims[id].Sender);}
    [Test] public void FailedOrExpiredAttemptReleasesReservation(){var id=Guid.NewGuid();var p=Claim(leader,id,1,true);state.Receive(p,1);Assert.IsTrue(state.DefenseReservedByOther(id,2));state.Tick(4);Assert.IsFalse(state.DefenseReservedByOther(id,4));p=Claim(leader,id,1);state.Receive(p,1);p.Kind=FleetPacketKind.DefenseRelease;p.Revision=2;state.Receive(p,2);Assert.IsFalse(state.DefenseReservedByOther(id,2));}
    [Test] public void ThreeThreatsAreThreeReservations(){for(int i=0;i<3;i++)state.Receive(Claim(leader,Guid.NewGuid(),1),1);Assert.AreEqual(3,state.Claims.Count);}
    [Test] public void ForeignFleetCannotReserveDefense(){var p=Claim(leader,Guid.NewGuid(),1);p.Fleet=Guid.NewGuid();Assert.IsFalse(state.Receive(p,1));Assert.AreEqual(0,state.Claims.Count);}
    static TrackSnapshot Track(int id,double x,double velocity=0){var t=new TrackSnapshot{TrackId=new TrackId(new Handle(id,1)),Position=new double3(x,0,0),Velocity=new double3(velocity,0,0),LastUpdateTime=1,LastPredictTime=1,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle};for(int i=0;i<6;i++)t.SetCovariance(i,i,25);return t;}
    [Test] public void ForeignTrackNumberIsNeverLocalIdentity(){var remote=Track(3,200000);var locals=new[]{Track(3,100000),Track(9,200001)};Assert.AreEqual(1,MechFleetContactMatcher.Match(remote,locals,2,1));}
    [Test] public void AmbiguousNearbyTargetsDoNotAuthorizeFire(){var remote=Track(3,200000);Assert.AreEqual(-1,MechFleetContactMatcher.Match(remote,new[]{Track(7,200001),Track(8,200002)},2,1));}
    [Test] public void StaleTargetReportCannotBeFreshenedByReception(){Assert.AreEqual(-1,MechFleetContactMatcher.Match(Track(3,200000),new[]{Track(7,200001)},1,10));}
    [Test] public void FriendlyLocalMatchNeverBecomesAttackTarget(){var t=Track(7,200000);t.Affiliation=TrackAffiliation.Friendly;Assert.AreEqual(-1,MechFleetContactMatcher.Match(Track(3,200000),new[]{t},1,1));}
    [Test] public void ThreatGeometryUsesProtectedMembersMotion(){var m=new MissileTrackSnapshot{Position=new double3(1000,0,0),Velocity=new double3(-100,0,0)};Assert.IsTrue(MechFleetController.Threatens(m,double3.zero,new double3(100,0,0),10,30,out float t));Assert.AreEqual(5,t,.001);Assert.IsFalse(MechFleetController.Threatens(m,new double3(0,1000,0),double3.zero,10,30,out _));}
    [Test] public void StaleTargetUpdateCannotOverwriteNewEvidence(){var p=Order();p.Target=Track(1,100000);state.Receive(p,1);p.Kind=FleetPacketKind.TargetUpdate;p.Target=Track(1,100005);p.Target.LastUpdateTime=2;Assert.IsTrue(state.Receive(p,2));p.Target.LastUpdateTime=1;Assert.IsFalse(state.Receive(p,2));Assert.AreEqual(2,state.Task.Order.Target.LastUpdateTime);}
    [Test] public void CancelRetryAcknowledgesSameCancellation(){var p=Order();state.Receive(p,1);p.Kind=FleetPacketKind.Cancel;p.Revision=2;state.Receive(p,2);FleetPacket reply=default;state.Feedback+=r=>reply=r;Assert.IsFalse(state.Receive(p,2.5));Assert.AreEqual(FleetTaskReason.Cancelled,reply.Reason);Assert.AreEqual(2,reply.Revision);}
    [Test] public void DuplicatePolicyChangeRetainsCompletedReceipt(){var p=Order(kind:FleetOrderKind.SetAttackMode);p.Mode=FleetAttackMode.Free;state.Receive(p,1);FleetPacket reply=default;state.Feedback+=r=>reply=r;state.Receive(p,2);Assert.AreEqual(FleetTaskPhase.Completed,reply.Phase);Assert.AreEqual(FleetTaskReason.None,reply.Reason);}
    [Test] public void ActualCommitWinsOverUncommittedBidRegardlessOfCost(){var contact=Guid.NewGuid();state.Receive(Claim(wing,contact,0),1);state.Receive(Claim(leader,contact,20,true),1);Assert.AreEqual(leader,state.Claims[contact].Sender);}
    [Test] public void UnresolvedRemoteCpaCannotPreventAnOtherwiseReachableFleetIntercept()
    {
        float deadline=MechFleetController.EarlierFutureDeadline(8.8f,0,.1);
        var track=new MissileTrackSnapshot{Position=new double3(0,0,8000),Velocity=new double3(0,0,-900),Quality=MissileTrackQuality.WeaponsQuality,LastUpdateTime=1,LastEvidenceTime=1,Incoming=false};
        var engine=new ACunicombat.FireControl.MissileKinematicProfile{EjectionSpeed=55,InertMass=58,MainPropellantMass=32,MainEngineThrust=48000,MainMassFlow=16};
        var solution=ACunicombat.FireControl.FireSolutionSolver.SolveInterceptor(track,double3.zero,double3.zero,new float3(0,0,1),engine,1,600,40,15000,protectedThreatTimeSeconds:deadline);
        Assert.IsTrue(solution.Reachable);Assert.Greater(solution.ThreatTimeMarginSeconds,0);Assert.IsFalse(track.Incoming,"Support context must not mutate sensor intent");
        var unprotected=ACunicombat.FireControl.FireSolutionSolver.SolveInterceptor(track,double3.zero,double3.zero,new float3(0,0,1),engine,1,600,40,15000);Assert.IsFalse(unprotected.Reachable);
    }
    [Test] public void LosingAliasBidDoesNotBlockTheWinningDefender()
    {
        var a=Claim(leader,Guid.NewGuid(),1);var b=Claim(wing,Guid.NewGuid(),3);var aliases=new System.Collections.Generic.HashSet<Guid>{a.Contact,b.Contact};
        Assert.IsTrue(MechFleetState.TrySelectDefenseClaim(new[]{b,a},aliases.Contains,1,out var first));Assert.AreEqual(leader,first.Sender);
        Assert.IsTrue(MechFleetState.TrySelectDefenseClaim(new[]{a,b},aliases.Contains,1,out var second));Assert.AreEqual(first.Sender,second.Sender);
        b.Kind=FleetPacketKind.DefenseCommitted;Assert.IsTrue(MechFleetState.TrySelectDefenseClaim(new[]{a,b},aliases.Contains,1,out var committed));Assert.AreEqual(wing,committed.Sender);
    }
    [Test] public void SelfContainedReservationIsChargedForItsObservation()
    {
        var packet=Claim(leader,Guid.NewGuid(),3);Assert.AreEqual(2,packet.EstimatedWireKilobits);
        packet.Threat=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(7,1)),Quality=MissileTrackQuality.WeaponsQuality,Position=new double3(0,0,2000)};
        packet.ThreatTimeSeconds=8;Assert.AreEqual(8,packet.EstimatedWireKilobits);Assert.AreNotEqual(packet.EstimatedSeconds,packet.ThreatTimeSeconds,"Interceptor TOF is not threat impact time");
    }
}

