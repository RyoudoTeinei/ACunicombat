using System;
using NUnit.Framework;
using UnityEngine;
using ACunicombat.Mechs;
public class MechOwnshipEventLogTests
{
 [TestCase(10,1,20,100,90000,"目标过近")]
 [TestCase(100,50,20,100,90000,"定位误差过大")]
 [TestCase(32000,1,20,36000,35000,"预测拦截点超出射程")]
 [TestCase(double.NaN,1,20,100,90000,"数据无效")]
 [TestCase(32000,1,20,36000,0,"配置无效")]
 public void GeometryRefusalShowsActualCondition(double range,double sigma,double safe,double intercept,double maximum,string expected)
 {Assert.IsFalse(MechMissileLaunchSource.CheckSafeGeometry(range,sigma,safe,intercept,maximum,out var reason));StringAssert.Contains(expected,MechOwnshipEventLog.Reason(reason));}
 [Test]public void GeometryRefusalIncludesMeasuredDistances()
 {MechMissileLaunchSource.CheckSafeGeometry(32430,1,20,36000,35000,out var reason);StringAssert.Contains("36.0 km",reason);StringAssert.Contains("35.0 km",reason);StringAssert.Contains("32.4 km",reason);Assert.IsTrue(MechMissileLaunchSource.CheckSafeGeometry(40000,1,20,28000,29000,out reason));Assert.IsNull(reason);}
 GameObject host;MechOwnshipEventLog log;
 [SetUp]public void Setup(){host=new GameObject("Log test");log=host.AddComponent<MechOwnshipEventLog>();}
 [TearDown]public void Cleanup(){UnityEngine.Object.DestroyImmediate(host);}
 class Executor:IMechActionExecutor {public bool ReadyNow,Fail;public int Committed;public MechActionBudget AvailableBudget=>new MechActionBudget(10,2);public bool Check(MechActionProposal p,out string reason){reason=Fail?"NoAmmo":null;return !Fail;}public bool Begin(MechActionProposal p,out string reason){reason=null;return true;}public bool Ready(MechActionProposal p)=>ReadyNow;public bool Commit(MechActionProposal p,out string reason){reason="Committed";Committed++;return true;}public void Cancel(MechActionProposal p){}}
 MechActionProposal Proposal()=>new MechActionProposal(new MechActionId(MechActionSource.Player,1),Guid.NewGuid(),MechPartSlot.LeftBackWeapon,MechActionKind.Fire,default,8);
 [Test]public void PendingRequestUpdatesToSuccessOnlyAfterCommit()
 {var e=new Executor();var a=new MechActionArbiter(e);a.FeedbackReported+=f=>log.Record(f,"左肩",true);a.Submit(Proposal(),0);a.Tick(0);Assert.AreEqual(1,log.Entries.Count);StringAssert.Contains("准备中",log.Entries[0].Text);Assert.AreEqual(0,e.Committed);e.ReadyNow=true;a.Tick(1);StringAssert.Contains("发射成功",log.Entries[0].Text);Assert.AreEqual(1,e.Committed);}
 [Test]public void AcceptedButLaterRejectedRequestExplainsFailure()
 {var e=new Executor();var a=new MechActionArbiter(e);a.FeedbackReported+=f=>log.Record(f,"左肩",true);a.Submit(Proposal(),0);a.Tick(0);e.Fail=true;a.Tick(1);Assert.AreEqual(1,log.Entries.Count);StringAssert.Contains("未发射",log.Entries[0].Text);StringAssert.Contains("弹药耗尽",log.Entries[0].Text);Assert.IsTrue(log.Entries[0].Warning);Assert.AreEqual(0,e.Committed);}
 [Test]public void HistoryIsBoundedAndRapidRepeatsCoalesce()
 {for(int i=0;i<1000;i++)log.AddAt("same","武器冷却中",i*.001,true);Assert.AreEqual(1,log.Entries.Count);Assert.AreEqual(1000,log.Entries[0].Count);for(int i=0;i<100;i++)log.AddAt("event"+i,"事件",i+10);Assert.AreEqual(64,log.Entries.Count);}
 [Test]public void LowEnergyHasSpecificReason()
 {var e=new Executor();var a=new MechActionArbiter(e);a.FeedbackReported+=f=>log.Record(f,"左肩",true);a.Submit(new MechActionProposal(new MechActionId(MechActionSource.Player,1),Guid.NewGuid(),MechPartSlot.LeftBackWeapon,MechActionKind.Fire,default,8,100),0);a.Tick(0);StringAssert.Contains("能源不足",log.Entries[0].Text);}
 [Test]public void GlobalDamageEventsOnlyReportThisOwnship()
 {var own=host.AddComponent<CombatTestTarget>();var other=new GameObject("Other mech");try{var foreign=other.AddComponent<CombatTestTarget>();var method=typeof(MechOwnshipEventLog).GetMethod("OnDamage",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);method.Invoke(log,new object[]{foreign,null,TestWeaponKind.Missile,10f});Assert.AreEqual(0,log.Entries.Count);method.Invoke(log,new object[]{own,null,TestWeaponKind.Missile,10f});Assert.AreEqual(1,log.Entries.Count);}finally{UnityEngine.Object.DestroyImmediate(other);}}
}
