using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
public sealed class MechMissileContinuityContractTests
{
    static MissileTrackSnapshot Track(double time,double x=0,byte generation=1)=>new MissileTrackSnapshot{TrackId=new TrackId(new Handle(1,generation)),Position=new double3(x,0,10000),Velocity=new double3(100,0,0),LastUpdateTime=time,LastEvidenceTime=time,Quality=MissileTrackQuality.Warning};
    [Test] public void ConstantMotionPresentationDoesNotAddVelocityLag()
    {var view=new MechMissileMarkerPresentation();for(int i=0;i<120;i++){double t=i/60.0;var track=Track(t,100*t);Assert.That(view.Sample(track,t).Position.x,Is.EqualTo((float)(100*t)).Within(.01));}}
    [Test] public void MeasurementCorrectionIsSmoothedOnlyInPresentation()
    {var view=new MechMissileMarkerPresentation();var initial=Track(0);view.Sample(initial,0);var revised=Track(.02,1002);var position=view.Sample(revised,.02).Position;Assert.That(position.x,Is.GreaterThan(2).And.LessThan(200));Assert.That(revised.Position.x,Is.EqualTo(1002));Assert.That(view.Sample(revised,.02).Position,Is.EqualTo(position),"Repeated OnGUI passes must not advance interpolation.");}
    [Test] public void ConfirmedSensorToTerminalAssociationKeepsPresentationHistory()
    {
        var view=new MechMissileMarkerPresentation();var raw=Track(0);view.Sample(raw,0);
        var linked=Track(.02,1002);linked.AssociatedSensorTrackId=raw.TrackId;linked.TrackId=new TrackId(new Handle(2005,1));
        float x=view.Sample(linked,.02).Position.x;Assert.That(x,Is.GreaterThan(2).And.LessThan(200));
        raw=Track(.04,1004);Assert.That(view.Sample(raw,.04).Position.x,Is.GreaterThan(x).And.LessThan(400),"Radio loss must retain the same sensor presentation history");
    }
    [Test] public void ReusedTrackIndexDoesNotInheritPreviousPresentation()
    {var view=new MechMissileMarkerPresentation();view.Sample(Track(0),0);var replaced=Track(.02,5000,2);Assert.That(view.Sample(replaced,.02).Position.x,Is.EqualTo(5000));}
    [Test] public void MissileRoleDoesNotFlashAtConfidenceBoundary()
    {var view=new MechMissileMarkerPresentation();var t=Track(0);t.TargetIntent=MissileTargetIntent.Ownship;t.TargetIntentConfidence=.8f;Assert.That(view.Sample(t,0).OwnshipThreat,Is.True);t.TargetIntentConfidence=.74f;Assert.That(view.Sample(t,.2).OwnshipThreat,Is.True);Assert.That(view.Sample(t,.6).OwnshipThreat,Is.False);}
    [Test] public void CoastingMarkerFadesRatherThanBlinkingAtPointSixSeconds()
    {var t=Track(0);Assert.That(MechMissileDisplayPolicy.ShouldDisplay(t,.61,false),Is.True);Assert.That(MechMissileDisplayPolicy.Opacity(t,.61),Is.InRange(.9f,1));Assert.That(MechMissileDisplayPolicy.ShouldDisplay(t,1.6,false),Is.False);}
    [Test] public void RetiredMissileStillDisappearsImmediately()
    {var t=Track(0);t.Affiliation=TrackAffiliation.Friendly;t.IdentityConfidence=1;Assert.That(MechMissileDisplayPolicy.ShouldDisplay(t,.02,true),Is.False);}
}
