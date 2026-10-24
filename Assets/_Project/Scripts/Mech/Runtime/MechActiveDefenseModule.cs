using System;
using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    // Local self-defense policy. It has no world-target access and never spawns or removes projectiles.
    [Serializable]
    public sealed class MechActiveDefenseModule
    {
        [NonSerialized] public Func<MissileTrackSnapshot,bool> FleetThreat;
        public bool Authorized=true;
        public float MinimumReactionSeconds;
        public double MaximumEvidenceAgeSeconds=2;
        public string Reason {get;private set;}="NoThreat";
        public MissileTrackSnapshot Selected {get;private set;}
        private double holdUntil;
        public bool Accepts(in MissileTrackSnapshot track,double now)
            => Authorized && track.TrackId.IsValid && track.IsWeaponsQuality && !track.IsFriendly && (track.Incoming || FleetThreat?.Invoke(track)==true) &&
               (FleetThreat?.Invoke(track)==true || float.IsFinite(track.TimeToClosestApproachSeconds) && track.TimeToClosestApproachSeconds>=MinimumReactionSeconds) &&
               now>=track.LastEvidenceTime && now-track.LastEvidenceTime<=MaximumEvidenceAgeSeconds;
        public MissileTrackSnapshot Select(MissileTrackSnapshot[] tracks,int count,double now,float holdSeconds,
            Func<MissileTrackSnapshot,bool> feasible,Func<MissileTrackSnapshot,float> score)
        {
            var best=default(MissileTrackSnapshot);float bestScore=float.PositiveInfinity;
            for(int i=0;i<Math.Min(count,tracks.Length);i++)
            {
                var track=tracks[i];if(!Accepts(in track,now)||!feasible(track))continue;
                if(track.TrackId.Equals(Selected.TrackId)&&now<holdUntil){Selected=track;Reason="Holding";return track;}
                float value=score(track);if(!float.IsFinite(value))continue;
                if(value<bestScore||(value==bestScore&&track.TrackId.Packed<best.TrackId.Packed)){best=track;bestScore=value;}
            }
            if(!best.TrackId.Equals(Selected.TrackId))holdUntil=now+Math.Max(0,holdSeconds);
            Selected=best;Reason=!Authorized?"NotAuthorized":best.TrackId.IsValid?"Selected":"NoFeasibleThreat";return best;
        }
        public void Clear(){Selected=default;holdUntil=0;Reason="Inactive";}
    }
}
