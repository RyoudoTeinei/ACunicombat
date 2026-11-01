using System;
namespace ACunicombat.Mechs
{
 // Leader succession. The configured leader (State.Leader) keeps protocol authority;
 // the *followed* leader falls back deterministically when its reports go stale or
 // it is lost: configured leader -> formation root -> lowest live formation index.
 // Every member computes the same answer from the same reports, so no election.
 public sealed partial class MechFleetController
 {
  /// <summary>The member this unit actually flies on; itself when it is the acting root.</summary>
  public Guid ActiveLeader=>Configured?(activeLeader!=Guid.Empty?activeLeader:State.Leader):Guid.Empty;
  private Guid activeLeader;
  public bool IsActingLeader=>Configured&&ActiveLeader==MemberId;
  public bool LeaderSucceeded=>Configured&&ActiveLeader!=State.Leader;

  private void UpdateSuccession(double now)
  {
   var previous=ActiveLeader;
   activeLeader=ResolveActiveLeader(now);
   if(previous!=ActiveLeader&&previous!=Guid.Empty)Log("leader_succession",default,previous.ToString("N").Substring(0,8)+"->"+ActiveLeader.ToString("N").Substring(0,8));
  }

  private bool LiveReport(Guid member,double now)
   =>member==MemberId?body==null||body.IsAlive:State.Members.TryGetValue(member,out var r)&&State.Fresh(r,now)&&r.Available;

  public const double SuccessionStartupGraceSeconds=3;
  private double firstSuccessionTick=double.NaN;

  private Guid ResolveActiveLeader(double now)
  {
   if(State.Leader==MemberId)return MemberId;
   if(LiveReport(State.Leader,now))return State.Leader;
   // A leader never heard yet is not lost: wait for the first heartbeat exchange.
   if(double.IsNaN(firstSuccessionTick))firstSuccessionTick=now;
   if(now-firstSuccessionTick<SuccessionStartupGraceSeconds&&!State.Members.ContainsKey(State.Leader))return State.Leader;
   if(State.FormationRoot!=Guid.Empty&&State.FormationRoot!=State.Leader&&LiveReport(State.FormationRoot,now))return State.FormationRoot;
   // Lowest live index in the same formation (including self) becomes acting root.
   Guid best=MemberId;int bestIndex=State.FormationIndex;
   foreach(var entry in Roster.Values)
   {
    if(entry.Member==MemberId||GroupRoot(entry)!=State.FormationRoot||entry.FormationIndex>=bestIndex)continue;
    if(!LiveReport(entry.Member,now))continue;
    best=entry.Member;bestIndex=entry.FormationIndex;
   }
   return best;
  }
 }
}
