using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    public enum MechTargetTrackingMode { Search, Focused, DataLink, Passive }
    public static class MechMissileLaunchSource
    {
        public static string Label(MechTargetTrackingMode mode,bool ready=false)
            => (mode==MechTargetTrackingMode.Focused?"跟踪":mode==MechTargetTrackingMode.DataLink?"数据链":mode==MechTargetTrackingMode.Passive?"被动":"搜索")+(ready?"·锁定":"");
        public static bool Fresh(TrackSourceEvidence evidence,double now,double lifetime)
            => evidence.Valid&&double.IsFinite(evidence.Timestamp)&&now>=evidence.Timestamp&&now-evidence.Timestamp<=lifetime;
        public static MechTargetTrackingMode Resolve(in TrackSnapshot track,bool radarEnabled,bool focused,bool passiveSupported,bool passiveAssociated,double now,bool preferPassiveRf=false)
        {
            if(preferPassiveRf)return MechTargetTrackingMode.Passive;
            if(radarEnabled&&focused)return MechTargetTrackingMode.Focused;
            if(passiveSupported&&passiveAssociated)return MechTargetTrackingMode.Passive;
            if(track.SourceEvidence.DataLink.Valid&&track.SourceEvidence.DataLink.Timestamp<=now)return MechTargetTrackingMode.DataLink;
            return radarEnabled?MechTargetTrackingMode.Search:MechTargetTrackingMode.Passive;
        }
        public static bool CanUse(MechTargetTrackingMode mode,bool passiveSupported)
            =>mode==MechTargetTrackingMode.Focused||mode==MechTargetTrackingMode.DataLink||mode==MechTargetTrackingMode.Passive&&passiveSupported;
        public static bool SafeGeometry(double currentRange,double positionSigma,double safeDistance,double interceptPath,double maximumPath)
            =>double.IsFinite(currentRange)&&double.IsFinite(positionSigma)&&double.IsFinite(interceptPath)&&
                positionSigma>=0&&currentRange>safeDistance+3*positionSigma&&maximumPath>0&&interceptPath<=maximumPath;
    }
}
