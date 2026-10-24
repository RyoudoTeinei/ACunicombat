using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private Func<IEnumerable<Contact>> scenarioContacts;
  private Action<string,Vector3,bool> scenarioMove;
  private Action<string,bool> scenarioSelect;
  private Func<Vector2,bool> scenarioUiHit;
  private string scenarioDragging;
  private Vector3 scenarioDragOffset;
  public bool ScenarioEditing=>scenarioContacts!=null;
  public void ConfigureScenarioEditor(Vector3 origin,Func<IEnumerable<Contact>> provider,Action<string,Vector3,bool> move,Action<string,bool> select,Func<Vector2,bool> uiHit)
  {
   scenarioContacts=provider;scenarioMove=move;scenarioSelect=select;scenarioUiHit=uiHit;
   transform.position=origin;missionGridOrigin=origin;missionGridRotation=Quaternion.identity;missionGridReady=true;
   ResetView();SetVisible(true);
   var bounds=new Bounds(origin,Vector3.zero);foreach(var c in provider())bounds.Encapsulate(c.Position);
   float availableWidth=Mathf.Max(300,layoutWidth-360),availableHeight=H-180;
   rangeKm=Mathf.Max(BaseRangeKm,bounds.size.x*440f/(availableWidth*1000f),bounds.size.z*440f/(availableHeight*1000f))*1.15f;
   pan=new Vector2(availableWidth*.5f,H*.5f-30)-Project(bounds.center);Refresh();
  }
  private void UpdateScenarioEditor()
  {
   if(!visible)return;EnsureDisplay();ResizeTargets();
   var mouse=Mouse.current;if(mouse==null)return;
   var screen=mouse.position.ReadValue();var top=new Vector2(screen.x,Screen.height-screen.y);
   var p=ScreenToLayout(top,viewport);mapPointer=p;
   bool blocked=scenarioUiHit!=null&&scenarioUiHit(top);
   if(!blocked)
   {
    float scroll=mouse.scroll.ReadValue().y;if(scroll!=0)ZoomAt(scroll,p);
    if(mouse.middleButton.isPressed)pan+=new Vector2(mouse.delta.ReadValue().x,-mouse.delta.ReadValue().y)*H/viewport.height;
    if(mouse.leftButton.wasPressedThisFrame||mouse.rightButton.wasPressedThisFrame)
    {
     string hit=null;Contact picked=default;float nearest=24;
     foreach(var c in contacts){float d=Vector2.Distance(p,Project(c.Position));if(d<nearest){nearest=d;hit=c.Key;picked=c;}}
     selected=hit;scenarioSelect?.Invoke(hit,mouse.rightButton.wasPressedThisFrame);
     if(hit!=null&&mouse.leftButton.wasPressedThisFrame){scenarioDragging=hit;scenarioDragOffset=picked.Position-MapToGridPlane(p);}
    }
   }
   if(scenarioDragging!=null)
   {
    if(mouse.leftButton.isPressed&&!blocked)scenarioMove?.Invoke(scenarioDragging,MapToGridPlane(p)+scenarioDragOffset,false);
    if(mouse.leftButton.wasReleasedThisFrame){scenarioMove?.Invoke(scenarioDragging,Vector3.zero,true);scenarioDragging=null;}
   }
   Refresh();
  }
 }
}
