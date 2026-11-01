using System;
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
            => CheckSafeGeometry(currentRange,positionSigma,safeDistance,interceptPath,maximumPath,out _);

        public static bool CheckSafeGeometry(double currentRange,double positionSigma,double safeDistance,double interceptPath,double maximumPath,out string reason)
        {
            reason=null;
            if(!double.IsFinite(currentRange)||!double.IsFinite(positionSigma)||!double.IsFinite(interceptPath)||positionSigma<0)
                reason="目标定位或拦截解数据无效，请等待航迹更新";
            else if(!(currentRange>safeDistance+3*positionSigma))
            {
                double required=safeDistance+3*positionSigma;
                reason=currentRange<=safeDistance
                    ? FormattableString.Invariant($"目标过近：距离 {currentRange/1000:F2} km，安全距离须大于 {safeDistance/1000:F2} km")
                    : FormattableString.Invariant($"定位误差过大：距离 {currentRange/1000:F2} km，安全要求须大于 {required/1000:F2} km（基础 {safeDistance/1000:F2}＋3σ {3*positionSigma/1000:F2}）");
            }
            else if(!(maximumPath>0))
                reason="导弹最大射程配置无效";
            else if(!(interceptPath<=maximumPath))
                reason=FormattableString.Invariant($"预测拦截点超出射程：距本机 {interceptPath/1000:F1} km，导弹上限 {maximumPath/1000:F1} km（目标当前 {currentRange/1000:F1} km）");
            return reason==null;
        }
    }
}
