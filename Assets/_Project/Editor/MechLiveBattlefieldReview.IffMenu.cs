using System;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEditor;
using UnityEngine;
public static partial class MechLiveBattlefieldReview
{
 static int iffStage,iffFrames;
 public static void RunIffMenuReview(){SessionState.SetBool("LiveBattlefieldReview.iffMenu",true);RunRenderingFixReview();}
 static void TickIffMenu(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  if(EditorApplication.timeSinceStartup-entered>100)throw new Exception("IFF menu review timed out");
  if(Time.timeAsDouble<12)return;
  map.SetVisible(true);map.Refresh();
  if(++iffFrames<10)return;iffFrames=0;
  if(iffStage==0)
  {
   var target=map.Contacts.FirstOrDefault(c=>!c.Own&&!c.Missile&&c.Observation.TrackId.IsValid);
   if(string.IsNullOrEmpty(target.Key))return;
   typeof(MechBattlefieldMap).GetField("contextTarget",MapPrivate).SetValue(map,target);
   typeof(MechBattlefieldMap).GetField("contextPoint",MapPrivate).SetValue(map,new Vector2(650,250));
   typeof(MechBattlefieldMap).GetField("contextOpen",MapPrivate).SetValue(map,true);
   ClickMapCommand(map,"标记 ▸");
  }
  else if(iffStage==1){map.Capture(dir+"/identity-menu.png");ClickMapCommand(map,"中立");}
  else if(iffStage==2)
  {
   var target=(MechBattlefieldMap.Contact)typeof(MechBattlefieldMap).GetField("contextTarget",MapPrivate).GetValue(map);
   if(!rig.GetComponent<MechIdentityController>().TryGetIdentity(target.Observation.TrackId,out var state)||state.Relation!=IdentityRelation.Neutral)throw new Exception("Menu manual mark did not update identity");
   ClickMapCommand(map,"IFF 问询");
  }
  else if(iffStage==3)
  {
   map.Capture(dir+"/iff-status.png");Debug.Log("IFF_MENU_PASS: manual neutral via menu, query action and live status rendering");
   SessionState.SetBool("LiveBattlefieldReview.iffMenu",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");EditorApplication.isPlaying=false;
  }
  iffStage++;
 }
}
