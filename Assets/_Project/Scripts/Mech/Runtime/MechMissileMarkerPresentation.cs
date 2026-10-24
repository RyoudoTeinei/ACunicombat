using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    // Render-only correction filtering. Never feeds radar, guidance, identity, or interception.
    public sealed class MechMissileMarkerPresentation
    {
        public struct Result { public Vector3 Position; public bool OwnshipThreat,AirToAir; }
        private sealed class State {public double Time,LastSeen,ThreatUntil,RoleUntil;public Vector3 Position,Velocity;}
        private readonly Dictionary<TrackId,State> states=new Dictionary<TrackId,State>();
        private readonly List<TrackId> stale=new List<TrackId>();
        private double nextPrune;
        public Result Sample(in MissileTrackSnapshot track,double now)
        {
            // A render interpolation time can precede a newly launched weapon's birth.
            // Do not extrapolate it backwards through the launcher or advance its
            // smoothing history over time during which it did not yet exist.
            if(track.OwnLaunchIdentity!=Guid.Empty)now=Math.Max(now,track.FirstObservationTime);
            double relativeTime=Math.Max(-.04,now-track.LastUpdateTime);
            var measured=(Vector3)(float3)(track.Position+track.Velocity*relativeTime+.5*track.Acceleration*relativeTime*relativeTime);
            var velocity=(Vector3)(float3)track.PredictVelocity(now);
            if(!states.TryGetValue(track.TrackId,out var s))
            {
                // A confirmed sensor-to-terminal association changes the public ID,
                // not the physical object. Keep its existing presentation history.
                if(!track.AssociatedSensorTrackId.IsValid || !states.TryGetValue(track.AssociatedSensorTrackId,out s))
                    s=new State{Position=measured,Velocity=velocity,Time=now,ThreatUntil=-1,RoleUntil=-1};
                states.Add(track.TrackId,s);
            }
            if(track.AssociatedSensorTrackId.IsValid)states[track.AssociatedSensorTrackId]=s;
            double dt=now-s.Time;
            if(dt<0||dt>2){s.Position=measured;s.ThreatUntil=s.RoleUntil=-1;}
            else if(dt>1e-8)
            {
                var predicted=s.Position+s.Velocity*(float)dt;
                s.Position=Vector3.Lerp(predicted,measured,1-Mathf.Exp(-(float)dt/.15f));
            }
            s.Time=now;s.LastSeen=now;s.Velocity=velocity;
            if(track.IsLikelyTargetingOwnship)s.ThreatUntil=now+.5;
            if(track.IsLikelyAirToAir)s.RoleUntil=now+.5;
            if(now>=nextPrune){nextPrune=now+1;stale.Clear();foreach(var pair in states)if(now-pair.Value.LastSeen>4)stale.Add(pair.Key);foreach(var id in stale)states.Remove(id);}
            return new Result{Position=s.Position,OwnshipThreat=now<s.ThreatUntil,AirToAir=now<s.RoleUntil};
        }
    }
}
