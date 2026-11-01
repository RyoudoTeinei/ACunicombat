using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    public enum IdentityRelation : byte { Unknown, Friendly, Neutral, SuspectedHostile, Hostile }
    public enum IdentityParty : byte { Unknown, OwnAlliance, ThirdParty }
    public enum IdentityReason : byte { None, Authenticated, CooperativeReport, MissionIntelligence, SustainedIllumination, ObservedAttack, EvidenceConflict, EvidenceExpired, CombinedBehavior, FormationAssociation }
    public enum IffOutcome : byte { None, Waiting, Authenticated, AuthenticatedUnassociated, NoResponse, Untrusted, InvalidProof, Replay, Expired, Corrupt, Cancelled, CapacityLimited }
    [Serializable]
    public struct ContactIdentitySnapshot
    {
        public bool Available, Conflict, UnderAttack, CanAttackPlatform, CanInterceptThreat;
        public IdentityRelation Relation;
        public IdentityParty Party;
        public IdentityReason Reason;
        public IffOutcome Iff;
        public float Confidence;
        /// <summary>独立于阵营结论的行为可疑度，0–1；不表示攻击许可。</summary>
        public float Suspicion;
        public double EvidenceTime, ValidUntil;
    }

    public enum TrackLifecycle : byte
    {
        Tentative = 0,
        Confirmed = 1,
        Coasting = 2,
        Degraded = 3,
        Dropped = 4,
    }

    public enum TrackAffiliation : byte
    {
        Unknown = 0,
        Friendly = 1,
        Hostile = 2,
    }

    public enum TrackObjectClass : byte
    {
        Unknown = 0,
        AirVehicle = 1,
        Missile = 2,
        /// <summary>雷达或红外诱饵；不是可交战平台航迹。</summary>
        Decoy = 3,
    }

    public enum MissileMissionRole : byte
    {
        Unknown = 0,
        AirToAir = 1,
        Interceptor = 2,
    }

    [Flags]
    public enum TrackSourceMask : uint
    {
        None = 0,
        Radar = 1 << 0,
        ElectroOptical = 1 << 1,
        Esm = 1 << 2,
        DataLink = 1 << 3,
        LaserRangefinder = 1 << 4,
        CloseProtectionRadar = 1 << 5,
        Iff = 1 << 6,
    }

    public enum ObservedWeightClass : byte { Unknown, Light, Medium, Heavy }
    public enum ObservedCombatRole : byte { Unknown, AirToAir, AirDefense, Reconnaissance, LongRangeAirToAir }

    [Serializable]
    public struct TrackSourceEvidence
    {
        public float Confidence; public double Timestamp; public bool Valid;
        public float Score(double now,double lifetime=3){double age=math.max(0,now-Timestamp);return Valid&&lifetime>0&&age<lifetime?math.saturate(Confidence)*(float)(1-age/lifetime):-1;}
    }
    [Serializable]
    public struct TrackSourceEvidenceSet
    {
        public TrackSourceEvidence Radar,ElectroOptical,Esm,DataLink,Laser,CloseProtection,Iff;
        public TrackSourceEvidence Get(TrackSourceMask s){switch(s){case TrackSourceMask.Radar:return Radar;case TrackSourceMask.ElectroOptical:return ElectroOptical;case TrackSourceMask.Esm:return Esm;case TrackSourceMask.DataLink:return DataLink;case TrackSourceMask.LaserRangefinder:return Laser;case TrackSourceMask.Iff:return Iff;case TrackSourceMask.CloseProtectionRadar:return CloseProtection;default:return default;}}
        public void Record(TrackSourceMask s,float confidence,double time)
        {
            var old=Get(s);if(old.Valid&&time<old.Timestamp)return;
            var v=new TrackSourceEvidence{Confidence=math.saturate(confidence),Timestamp=time,Valid=true};
            switch(s){case TrackSourceMask.Radar:Radar=v;break;case TrackSourceMask.ElectroOptical:ElectroOptical=v;break;case TrackSourceMask.Esm:Esm=v;break;case TrackSourceMask.DataLink:DataLink=v;break;case TrackSourceMask.LaserRangefinder:Laser=v;break;case TrackSourceMask.Iff:Iff=v;break;case TrackSourceMask.CloseProtectionRadar:CloseProtection=v;break;}
        }
    }

    /// <summary>
    /// 航迹快照（设计文档 6.2 Track 的对外只读形态）：火控/AI/UI/数据链消费的唯一状态。
    /// 协方差为 6×6 对称阵上三角 21 个 double，索引见 <see cref="CovarianceIndex"/>。
    /// </summary>
    [Serializable]
    public unsafe struct TrackSnapshot
    {
        public Unity.Collections.FixedString128Bytes KnownMissileModel;
        public TrackId TrackId;
        public PlatformId OwnerPlatformId;
        public double3 Position;
        public double3 Velocity;
        public fixed double CovarianceUpperTriangular[21];
        public float Confidence;
        public TrackObjectClass ObjectClass;
        public MissileMissionRole MissileRole;
        public float MissileRoleConfidence;
        public float Classification;
        public float IdentityConfidence;
        public TrackAffiliation Affiliation;
        public double FirstObservationTime;
        public bool HasMissileLaunchTime;
        public double MissileLaunchTime;
        public double LastUpdateTime;
        public double LastCloseProtectionTime;
        public double LastPredictTime;
        public int CoastCount;
        public int HitCount;
        public ContactIdentitySnapshot Identity;
        public TrackSourceMask SourceMask;
        public TrackSourceEvidenceSet SourceEvidence;
        public bool IsIdentifiedMech;
        public ObservedWeightClass IdentifiedWeight;
        public ObservedCombatRole IdentifiedRole;
        public float ConsistencyScore;
        public float InterferenceScore;
        public TrackLifecycle Lifecycle;
        // Historical state can remain queryable without accepting or publishing new associations.
        public bool AssociationClosed;

        public double3 PredictPosition(double time)
        {
            var propagationSeconds = math.max(
                0.0,
                time - LastPredictTime);
            return Position + Velocity * propagationSeconds;
        }

        /// <summary>Covariance of PredictPosition(time), at the same explicit epoch.</summary>
        public double3x3 PredictPositionCovariance(double time)
        {
            double dt=math.max(0,time-LastPredictTime);
            var result=double3x3.zero;
            for(int row=0;row<3;row++)for(int column=0;column<3;column++)
                result[column][row]=GetCovariance(row,column)
                    +dt*(GetCovariance(row,column+3)+GetCovariance(row+3,column))
                    +dt*dt*GetCovariance(row+3,column+3);
            return result;
        }

        public double PredictPositionUncertainty(double time)
        {
            var p=PredictPositionCovariance(time);
            return math.sqrt(math.max(0,p.c0.x)+math.max(0,p.c1.y)+math.max(0,p.c2.z));
        }

        /// <summary>6×6 对称阵上三角行主序索引：i ≤ j，行 i 的起点 = 6i − i(i−1)/2。</summary>
        public static int CovarianceIndex(int i, int j)
        {
            if (i > j)
            {
                (i, j) = (j, i);
            }

            return 6 * i - i * (i - 1) / 2 + (j - i);
        }

        public double GetCovariance(int i, int j)
        {
            var index = CovarianceIndex(i, j);
            fixed (double* values = CovarianceUpperTriangular)
            {
                return values[index];
            }
        }

        public void SetCovariance(int i, int j, double value)
        {
            var index = CovarianceIndex(i, j);
            fixed (double* values = CovarianceUpperTriangular)
            {
                values[index] = value;
            }
        }
    }
}
