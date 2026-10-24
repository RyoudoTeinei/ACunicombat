using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using UnityEngine;

public sealed class MechGuidanceUplinkCapacityTests
{
    private GameObject root;
    private MechRadarController radar;
    private readonly List<Object> owned=new List<Object>();
    private static TrackId Target => new TrackId(new Handle(1,1));
    private static LinkTerminalId Terminal(int i,int generation=1)=>new LinkTerminalId(new Handle((ushort)i,(byte)generation));
    [SetUp] public void Setup()
    {
        root=new GameObject("Uplink capacity test");root.SetActive(false);radar=root.AddComponent<MechRadarController>();
        Configure(16,24,512);
        // Budget-only fixture: pre-authorized endpoints. Real registration and receive-only
        // proxy rejection are exercised by MechMissileTrackLifecycleTests.
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var members=(bool[])typeof(MechRadarController).GetField("missileLinkControlMember",flags).GetValue(radar);
        var allocated=(bool[])typeof(MechRadarController).GetField("missileLinkAllocated",flags).GetValue(radar);
        var generations=(byte[])typeof(MechRadarController).GetField("missileLinkGenerations",flags).GetValue(radar);
        for(int i=1;i<41;i++){members[i]=true;allocated[i]=true;generations[i]=1;}
    }
    [TearDown] public void Cleanup(){Object.DestroyImmediate(root);foreach(var o in owned)Object.DestroyImmediate(o);owned.Clear();}
    private static MechAggregatedStats Stats(int support,int nodes,float bandwidth)
    {
        var s=new MechAggregatedStats();s.fireControlPerformance.guidanceChannelCapacity=3;
        s.fireControlPerformance.solutionUpdateRateHz=30;s.fireControlPerformance.missileSupportCapacity=support;
        s.dataLinkPerformance=MechDataLinkPerformance.CreateStandardFallback();
        s.dataLinkPerformance.maximumNetworkNodes=nodes;s.dataLinkPerformance.bandwidthKbps=bandwidth;return s;
    }
    private void Configure(int support,int nodes,float bandwidth)
    {
        var stats=Stats(support,nodes,bandwidth);
        typeof(MechRadarController).GetField("fireControlPerformance",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(radar,
            MechFireControlPerformanceModel.BuildFireControlProfile(stats,MechFireControlPerformanceModel.BuildRadarProfile(stats)));
        typeof(MechRadarController).GetField("dataLinkPerformance",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(radar,stats.dataLinkPerformance);
    }
    [TestCase(16,24,512,16)]
    [TestCase(32,24,512,23)]
    [TestCase(32,40,64,8)]
    [TestCase(40,50,512,40)]
    [TestCase(0,24,512,0)]
    public void CapacityCombinesFcsNodesAndTransportBandwidth(int fcs,int nodes,float bandwidth,int expected)
    {Configure(fcs,nodes,bandwidth);Assert.That(radar.GuidanceUplinkCapacity,Is.EqualTo(expected));}
    [Test] public void MoreThanEightMissilesSubscribeAndCapacityRejectsExplicitly()
    {
        for(int i=1;i<=16;i++)Assert.That(radar.BeginGuidanceUplink(Terminal(i),Target),Is.True,$"missile {i}");
        Assert.That(radar.GuidanceUplinksUsed,Is.EqualTo(16));
        Assert.That(radar.BeginGuidanceUplink(Terminal(17),Target),Is.False);
        Assert.That(radar.LastGuidanceUplinkRejection,Does.Contain("16/16"));
        radar.EndGuidanceUplink(Terminal(5));
        Assert.That(radar.BeginGuidanceUplink(Terminal(17),Target),Is.True);
        Assert.That(radar.GuidanceUplinksUsed,Is.EqualTo(16));
    }
    [Test] public void DuplicateSubscriptionDoesNotConsumeAnotherSlot_AndGenerationMatters()
    {
        Assert.That(radar.BeginGuidanceUplink(Terminal(1),Target),Is.True);
        Assert.That(radar.BeginGuidanceUplink(Terminal(1),new TrackId(new Handle(2,1))),Is.True);
        Assert.That(radar.GuidanceUplinksUsed,Is.EqualTo(1));
        radar.EndGuidanceUplink(Terminal(1,2));Assert.That(radar.GuidanceUplinksUsed,Is.EqualTo(1));
        radar.EndGuidanceUplink(Terminal(1));Assert.That(radar.GuidanceUplinksUsed,Is.Zero);
    }
    [Test] public void ReassemblyRefreshesEquipmentCapacityAndRemovalDisablesUplink()
    {
        var state=root.AddComponent<MechRuntimeState>();
        var loadout=ScriptableObject.CreateInstance<MechLoadoutDefinition>();owned.Add(loadout);
        var link=ScriptableObject.CreateInstance<MechPartDefinition>();owned.Add(link);
        var fcs=ScriptableObject.CreateInstance<MechPartDefinition>();owned.Add(fcs);
        var records=new List<MechAssemblyRecord>{new MechAssemblyRecord{slot=MechPartSlot.DataLinkTerminal,part=link},new MechAssemblyRecord{slot=MechPartSlot.FireControlSystem,part=fcs}};
        state.Apply(loadout,Stats(16,24,512),null,records,null);
        for(int i=1;i<=12;i++)Assert.That(radar.BeginGuidanceUplink(Terminal(i),Target),Is.True);
        state.Apply(loadout,Stats(4,24,512),null,records,null);
        Assert.That(radar.GuidanceUplinkCapacity,Is.EqualTo(4));Assert.That(radar.GuidanceUplinksUsed,Is.EqualTo(4));
        state.Apply(loadout,Stats(32,40,512),null,records,null);
        Assert.That(radar.GuidanceUplinkCapacity,Is.EqualTo(32));
        Assert.That(radar.BeginGuidanceUplink(Terminal(20),Target),Is.True);
        records.RemoveAt(0);state.Apply(loadout,Stats(32,40,512),null,records,null);
        Assert.That(radar.GuidanceUplinkCapacity,Is.Zero);Assert.That(radar.GuidanceUplinksUsed,Is.Zero);
    }
    [Test] public void LegacyFcsSupportSettingSurvivesAggregation()
    {
        var s=new MechAggregatedStats{guidanceChannels=3};
        s.AddFireControlPerformance(new MechFireControlPerformance{missileSupportCapacity=32});
        var p=MechFireControlPerformanceModel.BuildFireControlProfile(s,MechFireControlPerformanceModel.BuildRadarProfile(s));
        Assert.That(p.MissileSupportCapacity,Is.EqualTo(32));
        Assert.That(p.GuidanceChannelCapacity,Is.EqualTo(3));
    }
    [Test] public void DisabledUpdateRateAndFcsGuidanceHaveZeroCapacity()
    {
        var s=Stats(16,24,512);var p=MechFireControlPerformanceModel.BuildFireControlProfile(s,MechFireControlPerformanceModel.BuildRadarProfile(s));
        s.dataLinkPerformance.guidanceUpdateRateHz=0;
        Assert.That(MechFireControlPerformanceModel.GuidanceUplinkCapacity(s.dataLinkPerformance,p,4),Is.Zero);
        s.dataLinkPerformance.guidanceUpdateRateHz=4;s.fireControlPerformance.guidanceChannelCapacity=0;
        p=MechFireControlPerformanceModel.BuildFireControlProfile(s,MechFireControlPerformanceModel.BuildRadarProfile(s));
        Assert.That(MechFireControlPerformanceModel.GuidanceUplinkCapacity(s.dataLinkPerformance,p,4),Is.Zero);
    }
}
