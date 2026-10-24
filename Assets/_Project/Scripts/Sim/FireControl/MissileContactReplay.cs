using System;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.FireControl
{
    // Explicit wire IDs: JsonUtility does not serialize readonly handle fields or Guid.
    // This records sensor products and received reports, never scene truth or entity IDs.
    [Serializable] public sealed class MissileReplayTrack
    {
        public MissileTrackSnapshot state;
        public uint id, sensor, terminal;
        public string launch;
        public static MissileReplayTrack From(MissileTrackSnapshot s)=>new MissileReplayTrack
        {state=s,id=s.TrackId.Packed,sensor=s.AssociatedSensorTrackId.Packed,terminal=s.ReportingTerminal.Value.Packed,launch=s.OwnLaunchIdentity.ToString()};
        public MissileTrackSnapshot Restore()
        {
            var s=state;s.TrackId=Track(id);s.AssociatedSensorTrackId=Track(sensor);
            s.ReportingTerminal=new LinkTerminalId(Handle(terminal));s.OwnLaunchIdentity=Guid.Parse(launch);return s;
        }
        internal static Handle Handle(uint v)=>new Handle((int)(v&0xffffff),(byte)(v>>24));
        internal static TrackId Track(uint v)=>new TrackId(Handle(v));
    }
    [Serializable] public sealed class MissileReplayReport
    {
        public int slot;public uint terminal;public FriendlyMissileLinkReport state;
    }
    [Serializable] public sealed class MissileReplayBinding
    {
        public int slot;public uint track;public double authenticated,supported,score;
    }
    [Serializable] public sealed class MissileReplayLaunch
    {
        public string id;public double3 position,velocity;public double time,launchTime;
        public uint track,contact,terminal,target;public MissileMissionRole role;
    }
    [Serializable] public sealed class MissileContactReplayFrame
    {
        public int version=1,nextIdentity;
        public string lossless;
        public double time;public double3 observer;
        public MissileReplayTrack[] observations,expected;
        public MissileReplayReport[] reports,retiredReports;
        public uint[] retiredTracks;
        public MissileReplayBinding[] bindings;
        public MissileReplayLaunch[] launches;

        public static MissileContactReplayFrame Capture(MissileContactManager manager,double now,double3 observer,MissileTrackSnapshot[] input,int count)
        {
            var retired=manager.Retired.ToArray();
            return new MissileContactReplayFrame
            {
                time=now,observer=observer,nextIdentity=manager.NextOwnIdentity,
                observations=input.Take(count).Select(MissileReplayTrack.From).ToArray(),
                reports=manager.Reports.Select((s,i)=>new MissileReplayReport{slot=i,terminal=s.SourceTerminal.Value.Packed,state=s}).Where(r=>r.state.Active).ToArray(),
                bindings=manager.Associations.Select((s,i)=>new MissileReplayBinding{slot=i,track=s.TrackId.Packed,authenticated=s.LastAuthenticatedTime,supported=s.LastSupportedTime,score=s.LastAssociationScore}).Where(r=>r.track!=0).ToArray(),
                launches=manager.OwnLaunches.Values.Select(s=>new MissileReplayLaunch{id=s.Id.ToString(),position=s.Position,velocity=s.Velocity,time=s.Time,launchTime=s.LaunchTime,track=s.Track.Packed,contact=s.ContactId.Packed,terminal=s.Terminal.Value.Packed,target=s.AssignedTarget.Packed,role=s.Role}).ToArray(),
                retiredTracks=retired.Select(p=>p.Key.Packed).ToArray(),
                retiredReports=retired.Select(p=>new MissileReplayReport{terminal=p.Value.SourceTerminal.Value.Packed,state=p.Value}).ToArray()
            };
        }

        public MissileReplayTrack[] Replay()
        {
            if(version!=1)throw new InvalidOperationException("Unsupported contact replay schema");
            var manager=new MissileContactManager{NextOwnIdentity=nextIdentity};
            foreach(var r in reports){var s=r.state;s.SourceTerminal=new LinkTerminalId(MissileReplayTrack.Handle(r.terminal));manager.Reports[r.slot]=s;}
            foreach(var b in bindings)manager.Associations[b.slot]=new FriendlyMissileIdentityAssociation{TrackId=MissileReplayTrack.Track(b.track),LastAuthenticatedTime=b.authenticated,LastSupportedTime=b.supported,LastAssociationScore=b.score};
            foreach(var r in launches){var id=Guid.Parse(r.id);manager.OwnLaunches[id]=new OwnLaunchRecord{Id=id,Position=r.position,Velocity=r.velocity,Time=r.time,LaunchTime=r.launchTime,Track=MissileReplayTrack.Track(r.track),ContactId=MissileReplayTrack.Track(r.contact),AssignedTarget=MissileReplayTrack.Track(r.target),Terminal=new LinkTerminalId(MissileReplayTrack.Handle(r.terminal)),Role=r.role};}
            for(int i=0;i<retiredTracks.Length;i++){var r=retiredReports[i];var s=r.state;s.SourceTerminal=new LinkTerminalId(MissileReplayTrack.Handle(r.terminal));manager.Retired[MissileReplayTrack.Track(retiredTracks[i])]=s;}
            var output=new MissileTrackSnapshot[MissileContactManager.MaximumContacts];
            for(int i=0;i<observations.Length;i++)output[i]=observations[i].Restore();
            int count=manager.ResolveIdentities(time,observer,output,observations.Length);
            return output.Take(count).Select(MissileReplayTrack.From).ToArray();
        }
        public MissileContactReplayFrame RestoreExact()=>string.IsNullOrEmpty(lossless)?this:ReplayValueCodec.Decode<MissileContactReplayFrame>(lossless);
        public bool Matches(out string detail)
        {
            if(!string.IsNullOrEmpty(lossless))return RestoreExact().Matches(out detail);
            var actual=Replay().OrderBy(t=>t.id).ToArray();var wanted=expected.OrderBy(t=>t.id).ToArray();
            if(actual.Length!=wanted.Length){detail=$"contact count {actual.Length} != {wanted.Length}";return false;}
            for(int i=0;i<actual.Length;i++)if(JsonUtility.ToJson(actual[i])!=JsonUtility.ToJson(wanted[i])){detail=$"contact {wanted[i].id} differs";return false;}
            detail="Exact deterministic output match";return true;
        }
    }
}
