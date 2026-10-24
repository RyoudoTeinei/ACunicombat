using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;

public class MechCompositionSchedulerTests
{
    static TrackSnapshot T(int index)=>new TrackSnapshot{TrackId=new TrackId(new Handle(index,1))};
    [TestCase(30)] [TestCase(60)] [TestCase(144)]
    public void AlternatingCandidatesAndBestRanksCannotOscillateAttention(int fps)
    {
        var s=new MechCompositionTargetScheduler();var a=T(1);var b=T(2);
        Assert.IsTrue(s.Select(0,a,default,default,default,out _));
        for(int frame=1;frame<fps*30;frame++)
        {
            double t=(double)frame/fps;
            var candidate=(frame/(fps/5))%2==0?a:b;
            Assert.IsTrue(s.Select(t,default,candidate,b,a,out var selected));
            Assert.AreEqual(a.TrackId,selected.TrackId);
        }
        // An intentional, persistent new candidate does eventually acquire attention.
        for(int frame=0;frame<=fps;frame++)s.Select(30+(double)frame/fps,default,b,a,a,out _);
        Assert.AreEqual(b.TrackId,s.CurrentId);
    }
    [Test] public void LockPreemptsCandidateAndSurvivesBriefLossWithoutFollowingOthers()
    {
        var s=new MechCompositionTargetScheduler();var a=T(1);var b=T(2);
        s.Select(0,a,default,default,default,out _);
        Assert.IsTrue(s.Select(.3,default,b,b,default,out var held));Assert.AreEqual(a.TrackId,held.TrackId);
        Assert.IsTrue(s.ProtectingLock);
        s.Select(.4,b,a,a,default,out var locked);Assert.AreEqual(b.TrackId,locked.TrackId);
        Assert.IsFalse(s.Select(1.3,default,default,default,default,out _));
    }
    [Test] public void FirstCandidateRequiresIndependentTimeAndResetDropsOldAttention()
    {
        var s=new MechCompositionTargetScheduler();var a=T(1);
        for(int i=0;i<100;i++)Assert.IsFalse(s.Select(0,default,a,default,default,out _));
        Assert.IsTrue(s.Select(.61,default,a,default,default,out _));
        s.Reset();Assert.IsFalse(s.CurrentId.IsValid);
    }
    [Test] public void RecycledHandleDoesNotInheritPreviousTarget()
    {
        var s=new MechCompositionTargetScheduler();var old=T(1);var fresh=T(1);fresh.TrackId=new TrackId(new Handle(1,2));
        s.Select(0,old,default,default,default,out _);
        s.Select(.2,default,fresh,default,default,out var held);Assert.AreEqual(old.TrackId,held.TrackId);
        s.Select(1,default,fresh,default,default,out _);
        s.Select(1.61,default,fresh,default,default,out var selected);Assert.AreEqual(fresh.TrackId,selected.TrackId);
    }
}
