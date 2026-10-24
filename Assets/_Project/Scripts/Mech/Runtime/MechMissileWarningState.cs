using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    // Presentation arbitration only. Consumes observed tracks; never changes identity or fire control.
    public sealed class MechMissileWarningState
    {
        public const double EvidenceHoldSeconds = 1.0;
        private readonly Dictionary<TrackId,double> deadlines = new Dictionary<TrackId,double>();
        private readonly List<TrackId> expired = new List<TrackId>();
        private double previousTime = double.NegativeInfinity;
        public bool MissileWarning { get; private set; }
        public int HeldIncomingTrackCount => deadlines.Count;
        public EsmThreatLevel Level { get; private set; }
        public void Reset(){deadlines.Clear();MissileWarning=false;Level=EsmThreatLevel.None;previousTime=double.NegativeInfinity;}
        public static bool IsIncomingEvidence(in MissileTrackSnapshot track,double now)
        {
            if(!track.TrackId.IsValid||!track.IsWarning||track.IsFriendly||track.SourceMask==MissileTrackSourceMask.None||
                !double.IsFinite(track.LastEvidenceTime)||now<track.LastEvidenceTime-1e-6||now-track.LastEvidenceTime>EvidenceHoldSeconds||
                !math.all(math.isfinite(track.Position))||!math.all(math.isfinite(track.Velocity)))return false;
            if(track.TargetIntent==MissileTargetIntent.FriendlyMissile&&track.TargetIntentConfidence>=.75f)return false;
            if(!float.IsFinite(track.TimeToClosestApproachSeconds)||track.TimeToClosestApproachSeconds<=0)return false;
            if(track.IsLikelyTargetingOwnship)return true;
            return track.Incoming&&float.IsFinite(track.TimeToClosestApproachSeconds)&&track.TimeToClosestApproachSeconds>0&&
                float.IsFinite(track.ClosestApproachDistanceMeters)&&track.ClosestApproachDistanceMeters>=0&&
                float.IsFinite(track.CpaUncertaintyMeters)&&track.ClosestApproachDistanceMeters<=750f+math.clamp(track.CpaUncertaintyMeters,0,750);
        }
        public EsmThreatLevel Evaluate(MissileTrackSnapshot[] tracks,int count,double now,EsmThreatLevel receiverLevel)
        {
            if(!double.IsFinite(now)){Reset();return Level;}
            if(now<previousTime)Reset();previousTime=now;
            for(int i=0;i<Math.Min(count,tracks.Length);i++)
            {
                var track=tracks[i];
                // Explicit re-identification or confirmed diversion revokes this track immediately.
                if(track.IsFriendly||(track.TargetIntent==MissileTargetIntent.FriendlyMissile&&track.TargetIntentConfidence>=.75f))
                {deadlines.Remove(track.TrackId);continue;}
                if(IsIncomingEvidence(track,now))
                {
                    double until=track.LastEvidenceTime+EvidenceHoldSeconds;
                    if(!deadlines.TryGetValue(track.TrackId,out double old)||until>old)deadlines[track.TrackId]=until;
                }
            }
            expired.Clear();foreach(var pair in deadlines)if(pair.Value<=now)expired.Add(pair.Key);
            foreach(var id in expired)deadlines.Remove(id);
            MissileWarning=deadlines.Count>0||receiverLevel==EsmThreatLevel.MissileActive;
            Level=MissileWarning?EsmThreatLevel.MissileActive:receiverLevel;return Level;
        }
    }
}
