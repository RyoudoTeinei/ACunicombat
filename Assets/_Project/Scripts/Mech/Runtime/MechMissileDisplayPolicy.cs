using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    // Presentation eligibility only. Does not grant fire-control or interception permission.
    public static class MechMissileDisplayPolicy
    {
        public static bool IsFresh(in MissileTrackSnapshot track, double now)
        {
            double age = now - track.LastEvidenceTime;
            return age >= -0.05 && age <= 0.6;
        }
        public static float Opacity(in MissileTrackSnapshot track,double now)
        {
            if(track.IsFriendly)return 1;
            double age=System.Math.Max(0,now-track.LastEvidenceTime);
            return age<=.6?1:UnityEngine.Mathf.Clamp01((float)((1.5-age)/.9));
        }
        public static bool ShouldDisplay(in MissileTrackSnapshot track, double now, bool retired)
        {
            if (!track.TrackId.IsValid || retired) return false;
            return track.IsFriendly || (track.IsWarning && now-track.LastEvidenceTime>=-.05 && now-track.LastEvidenceTime<=1.5);
        }
    }
}
