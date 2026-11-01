using System;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    public static class MechAutomaticTargetSafety
    {
        static bool ProtectedFriendly(in TrackSnapshot track)
            =>track.TrackId.IsValid&&track.Lifecycle!=TrackLifecycle.Dropped&&track.ObjectClass!=TrackObjectClass.Missile&&track.ObjectClass!=TrackObjectClass.Decoy&&
              (track.Affiliation==TrackAffiliation.Friendly&&track.IdentityConfidence>=.8f||track.Identity.Available&&!track.Identity.CanAttackPlatform&&(track.Identity.Relation==IdentityRelation.Friendly||track.Identity.Party==IdentityParty.OwnAlliance));
        public static bool ShotCrossesFriendly(double3 origin,double3 projectileVelocity,double horizon,double angularSigma,TrackSnapshot[] contacts,int count,double now,out TrackId conflict)
        {
            conflict=default;if(horizon<=0)return false;
            for(int i=0;i<Math.Min(count,contacts.Length);i++)
            {
                var friend=contacts[i];if(!ProtectedFriendly(friend))continue;
                var relative=friend.PredictPosition(now)-origin;var velocity=friend.Velocity-projectileVelocity;
                double speedSquared=math.lengthsq(velocity);if(speedSquared<1e-9)continue;
                double t=-math.dot(relative,velocity)/speedSquared;if(t<=0||t>horizon)continue;
                double sigma=friend.PredictPositionUncertainty(now+t);
                double margin=25+3*sigma+3*Math.Max(0,angularSigma)*math.length(projectileVelocity)*t;
                if(double.IsFinite(margin)&&math.lengthsq(relative+velocity*t)<=margin*margin){conflict=friend.TrackId;return true;}
            }
            return false;
        }
        public static bool NeedsDisambiguation(in TrackSnapshot target)
            =>target.ObjectClass!=TrackObjectClass.Missile&&target.ObjectClass!=TrackObjectClass.Decoy&&
              !(target.Identity.Available&&target.Identity.Relation==IdentityRelation.Hostile&&target.Identity.CanAttackPlatform&&target.Identity.Confidence>=.8f);
        // Observation-space safety gate. No actor registry, true position or team ID.
        // A coarse unknown report must not authorize fire through a known ally's
        // uncertainty region just because fusion has not assigned it a friendly ID.
        public static bool OverlapsFriendly(in TrackSnapshot target,TrackSnapshot[] contacts,int count,double now,out TrackId conflict)
        {
            conflict=default;if(!NeedsDisambiguation(target))return false;
            var position=target.PredictPosition(now);double sigma=target.PredictPositionUncertainty(now);
            if(!math.all(math.isfinite(position))||!double.IsFinite(sigma))return true;
            for(int i=0;i<Math.Min(count,contacts.Length);i++)
            {
                var friend=contacts[i];if(!ProtectedFriendly(friend))continue;
                var other=friend.PredictPosition(now);double otherSigma=friend.PredictPositionUncertainty(now);
                if(!math.all(math.isfinite(other))||!double.IsFinite(otherSigma))continue;
                double margin=25+3*Math.Sqrt(sigma*sigma+otherSigma*otherSigma);
                if(math.distancesq(position,other)<=margin*margin){conflict=friend.TrackId;return true;}
            }
            return false;
        }
    }
}
