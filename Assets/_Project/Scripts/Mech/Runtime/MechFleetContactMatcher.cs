using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Tracking;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    public static class MechFleetContactMatcher
    {
        public static int Match(in TrackSnapshot remote,TrackSnapshot[] locals,int count,double now,double maximumAge=3)
        {
            if(!remote.TrackId.IsValid||now-remote.LastUpdateTime>maximumAge||remote.LastUpdateTime>now+.01)return -1;
            int best=-1;double first=double.PositiveInfinity,second=double.PositiveInfinity;
            for(int i=0;i<Math.Min(count,locals.Length);i++)
            {
                var local=locals[i];if(!local.TrackId.IsValid||local.AssociationClosed||local.Lifecycle==TrackLifecycle.Dropped||now-local.LastUpdateTime>maximumAge||local.Affiliation==TrackAffiliation.Friendly)continue;
                if(remote.ObjectClass!=TrackObjectClass.Unknown&&local.ObjectClass!=TrackObjectClass.Unknown&&remote.ObjectClass!=local.ObjectClass)continue;
                var state=new FilterState{Position=local.PredictPosition(now)};Matrix6 covariance=default;
                double la=Math.Max(0,now-local.LastPredictTime),ra=Math.Max(0,now-remote.LastPredictTime);
                for(int row=0;row<3;row++)for(int col=0;col<3;col++)
                {state.P[row,col]=local.GetCovariance(row,col)+la*(local.GetCovariance(row,col+3)+local.GetCovariance(row+3,col))+la*la*local.GetCovariance(row+3,col+3);covariance[row,col]=remote.GetCovariance(row,col)+ra*(remote.GetCovariance(row,col+3)+remote.GetCovariance(row+3,col))+ra*ra*remote.GetCovariance(row+3,col+3);}
                for(int axis=0;axis<3;axis++)covariance[axis,axis]+=25;
                if(!Cmkf.TryCalculatePositionInnovationNis(state,remote.PredictPosition(now),covariance,out double score))continue;
                for(int axis=0;axis<3;axis++){double dv=local.Velocity[axis]-remote.Velocity[axis];score+=dv*dv/Math.Max(25,local.GetCovariance(axis+3,axis+3)+remote.GetCovariance(axis+3,axis+3));}
                if(score<first){second=first;first=score;best=i;}else if(score<second)second=score;
            }
            return first<=25&&second-first>=4?best:-1;
        }
        public static TrackSnapshot Missile(in MissileTrackSnapshot m,double now)
        {
            var t=new TrackSnapshot{TrackId=m.TrackId,Position=m.PredictPosition(now),Velocity=m.PredictVelocity(now),LastUpdateTime=m.LastEvidenceTime,LastPredictTime=now,ObjectClass=TrackObjectClass.Missile,Lifecycle=TrackLifecycle.Confirmed,Affiliation=m.Affiliation,Confidence=m.Confidence};
            for(int row=0;row<3;row++)for(int col=0;col<3;col++)t.SetCovariance(row,col,m.HasPositionCovariance?m.PositionCovariance[col][row]:row==col?m.PositionUncertaintyMeters*m.PositionUncertaintyMeters/3:0);
            for(int axis=0;axis<3;axis++)t.SetCovariance(axis+3,axis+3,Math.Max(25,m.VelocityUncertaintyMetersPerSecond*m.VelocityUncertaintyMetersPerSecond/3));return t;
        }
    }
}
