using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using NUnit.Framework;
public class MechAutomaticTargetSafetyTests
{
    static TrackSnapshot Track(int index,double3 position,double sigma)
    {
        var t=new TrackSnapshot{TrackId=new TrackId(new Handle(index,1)),Position=position,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle,Confidence=.9f,LastPredictTime=404.94};
        for(int i=0;i<3;i++)t.SetCovariance(i,i,sigma*sigma/3);
        return t;
    }
    static TrackSnapshot Friend(double3 position)
    {
        var t=Track(1000,position,5);t.Affiliation=TrackAffiliation.Friendly;t.IdentityConfidence=1;
        t.Identity=new ContactIdentitySnapshot{Available=true,Relation=IdentityRelation.Friendly,Party=IdentityParty.OwnAlliance,Confidence=1};return t;
    }
    [Test] public void RecordedCoarseUnknownNearFriendlyCannotAuthorizeAutomaticFire()
    {
        // 404.94 s failure: sigma 131.58 m, nearest friendly separation 353.87 m.
        var target=Track(30542,new double3(-111.0385,1819.7302,9130.9443),131.58313);
        var friend=Friend(target.Position+new double3(353.86606,0,0));
        Assert.IsTrue(MechAutomaticTargetSafety.OverlapsFriendly(target,new[]{friend},1,404.94,out var id));Assert.AreEqual(friend.TrackId,id);
    }
    [Test] public void PreciseSeparatedUnknownRemainsEligibleUnderExistingUnknownAttackPolicy()
    {
        var target=Track(2,new double3(1000,0,0),2);var friend=Friend(new double3(1100,0,0));
        Assert.IsFalse(MechAutomaticTargetSafety.OverlapsFriendly(target,new[]{friend},1,404.94,out _));
    }
    [Test] public void ConfirmedHostileAndMissileDefenseRetainExistingAuthorizationRules()
    {
        var target=Track(2,double3.zero,200);var friend=Friend(double3.zero);
        target.Identity=new ContactIdentitySnapshot{Available=true,Relation=IdentityRelation.Hostile,CanAttackPlatform=true,Confidence=1};
        Assert.IsFalse(MechAutomaticTargetSafety.OverlapsFriendly(target,new[]{friend},1,404.94,out _));
        target.Identity=default;target.ObjectClass=TrackObjectClass.Missile;
        Assert.IsFalse(MechAutomaticTargetSafety.OverlapsFriendly(target,new[]{friend},1,404.94,out _));
    }
    [Test] public void FriendlyMotionIsPredictedAtTheSameDecisionEpoch()
    {
        var target=Track(2,new double3(1000,0,0),5);var friend=Friend(double3.zero);friend.Velocity=new double3(100,0,0);
        Assert.IsFalse(MechAutomaticTargetSafety.OverlapsFriendly(target,new[]{friend},1,404.94,out _));
        Assert.IsTrue(MechAutomaticTargetSafety.OverlapsFriendly(target,new[]{friend},1,414.94,out _));
    }
    [Test] public void RecordedFutureFriendlyCrossingIsBlockedBeforeTheShot()
    {
        var friend=Friend(new double3(1410.89246,2006.447,-204.6694));friend.Velocity=new double3(265.006226,-22.4222755,78.00317);
        var origin=new double3(2848.12329,225.581512,7986.23975);var velocity=new double3(-66.03032,381.85437,-1796.47351);
        Assert.IsTrue(MechAutomaticTargetSafety.ShotCrossesFriendly(origin,velocity,8,0,new[]{friend},1,404.94,out _));
        Assert.IsFalse(MechAutomaticTargetSafety.ShotCrossesFriendly(origin,velocity,2,0,new[]{friend},1,404.94,out _),"A round that expires before the crossing is not a threat at that crossing.");
        friend.Position+=new double3(1000,0,0);
        Assert.IsFalse(MechAutomaticTargetSafety.ShotCrossesFriendly(origin,velocity,8,0,new[]{friend},1,404.94,out _));
    }
}
