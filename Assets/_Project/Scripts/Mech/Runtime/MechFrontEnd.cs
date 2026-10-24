using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace ACunicombat.Mechs
{
 public sealed class MechFrontEnd:MonoBehaviour
 {
  private MechBattlefieldMap editorMap;
  private GameObject hangarHost;
  private enum Page{Home,Scenes,Editor,Equipment,Settings}
  private Page page,equipmentReturn;
  private MechScenarioDefinition[] scenarios;
  private MechScenarioDefinition scenario;
  private MechScenarioStore.Document document;
  private MechPartCatalog catalog;
  private MechScenarioUnit selectedUnit,equipmentUnit;
  private MechLoadoutDefinition draft;
  private string message="",heightText="0";
  
  private bool unitMenu,loading;private Vector2 unitMenuPoint;
  private Font font;private GUIStyle titleStyle,labelStyle,buttonStyle,smallStyle,textStyle;
  private float volume=1;private int quality;private bool fullscreen;
  private const float Height=900;
  private float Width=>Screen.width*Height/Mathf.Max(1,Screen.height);
  private readonly Color textColor=new Color(.76f,.86f,.80f),lineColor=new Color(.16f,.29f,.23f);
  private void Awake()
  {
   Time.timeScale=1;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
   scenarios=Resources.LoadAll<MechScenarioDefinition>("Scenarios");catalog=MechLoadoutSelectionStore.LoadCatalog();
   if(scenarios.Length>0)ChooseScenario(scenarios[0]);
   volume=PlayerPrefs.GetFloat("frontend.volume",1);quality=PlayerPrefs.GetInt("frontend.quality",QualitySettings.GetQualityLevel());fullscreen=PlayerPrefs.GetInt("frontend.fullscreen",Screen.fullScreen?1:0)!=0;
   AudioListener.volume=volume;
   quality=Mathf.Clamp(quality,0,QualitySettings.names.Length-1);
   QualitySettings.SetQualityLevel(quality,true);
   if(!Application.isEditor)Screen.fullScreen=fullscreen;
  }
  private void ChooseScenario(MechScenarioDefinition value)
  {
   if(editorMap!=null){Destroy(editorMap.gameObject);editorMap=null;}
   scenario=value;document=MechScenarioStore.Load(value,out var warning);message=warning??"";selectedUnit=null;

  }
  private void Styles()
  {
   if(titleStyle!=null)return;font=Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei","SimHei","Consolas"},24);
   labelStyle=new GUIStyle(GUI.skin.label){font=font,fontSize=17,wordWrap=true};labelStyle.normal.textColor=textColor;
   smallStyle=new GUIStyle(labelStyle){fontSize=13};titleStyle=new GUIStyle(labelStyle){fontSize=34};
   buttonStyle=new GUIStyle(GUI.skin.button){font=font,fontSize=17,alignment=TextAnchor.MiddleCenter};buttonStyle.normal.textColor=textColor;
   textStyle=new GUIStyle(GUI.skin.textField){font=font,fontSize=16};
  }
  private bool Button(Rect r,string text,bool enabled=true)
  {bool previous=GUI.enabled;GUI.enabled=previous&&enabled;bool clicked=GUI.Button(r,text,buttonStyle);GUI.enabled=previous;return clicked;}
  private void Fill(Rect r,Color color){var previous=GUI.color;GUI.color=color;GUI.DrawTexture(r,Texture2D.whiteTexture);GUI.color=previous;}
  private void Line(Vector2 a,Vector2 b,Color color,float thickness=1)
  {
   if(Event.current.type!=EventType.Repaint)return;
   var matrix=GUI.matrix;var tint=GUI.color;
   try
   {
    GUI.color=color;
    GUI.matrix=matrix*Matrix4x4.TRS(new Vector3(a.x,a.y,0),Quaternion.Euler(0,0,Mathf.Atan2(b.y-a.y,b.x-a.x)*Mathf.Rad2Deg),Vector3.one);
    GUI.DrawTexture(new Rect(0,-thickness*.5f,Vector2.Distance(a,b),thickness),Texture2D.whiteTexture);
   }
   finally{GUI.matrix=matrix;GUI.color=tint;}
  }
  private void OnGUI()
  {
   Styles();if(page==Page.Equipment)return;if(page==Page.Editor){DrawScenarioEditor();return;}var old=GUI.matrix;GUI.matrix=Matrix4x4.Scale(Vector3.one*(Screen.height/Height));GUI.depth=-5000;
   Fill(new Rect(0,0,Width,Height),new Color(.015f,.025f,.021f,1));
   GUI.Label(new Rect(42,28,Width-84,54),page==Page.Home?"ACUNICOMBAT":page==Page.Scenes?"场景":page==Page.Editor?"场景编辑":page==Page.Equipment?"机库 · "+equipmentUnit.name:"设置",titleStyle);
   Line(new Vector2(42,92),new Vector2(Width-42,92),lineColor);
   if(loading){GUI.Label(new Rect((Width-400)/2,Height/2,400,50),"正在载入战场…",titleStyle);GUI.matrix=old;return;}
   switch(page){case Page.Home:DrawHome();break;case Page.Scenes:DrawScenes();break;case Page.Editor:DrawScenarioEditor();break;case Page.Settings:DrawSettings();break;}
   GUI.Label(new Rect(42,Height-42,Width-84,36),message,smallStyle);GUI.matrix=old;
  }
  private void DrawHome()
  {
   string[] labels={"场景","机库","设置","退出"};for(int i=0;i<labels.Length;i++)if(Button(new Rect((Width-300)/2,280+i*72,300,54),labels[i]))
   {
    message="";
    if(i==0)page=Page.Scenes;
    else if(i==1){var player=scenario?.units.FirstOrDefault(u=>u.player);if(player!=null)OpenEquipment(player,Page.Home);else message="未找到玩家配装资料";}
    else if(i==2)page=Page.Settings;
    else {
     #if UNITY_EDITOR
     UnityEditor.EditorApplication.isPlaying=false;
     #else
     Application.Quit();
     #endif
    }
   }
  }
  private void DrawScenes()
  {
   for(int i=0;i<scenarios.Length;i++)if(Button(new Rect(80,150+i*64,Width-160,50),(scenario==scenarios[i]?"●  ":"")+scenarios[i].displayName))ChooseScenario(scenarios[i]);
   if(scenario!=null)GUI.Label(new Rect(80,240,Width-160,70),$"{scenario.units.Count} 个单位 · 可编辑开局位置与 NPC 配装",labelStyle);
   if(Button(new Rect(80,Height-120,140,48),"返回"))page=Page.Home;
   if(Button(new Rect(Width-390,Height-120,140,48),"编辑",scenario!=null)){page=Page.Editor;unitMenu=false;}
   if(Button(new Rect(Width-230,Height-120,150,48),"开始",scenario!=null))StartBattle();
  }
  private void StartBattle()
  {
   if(!MechScenarioStore.Begin(scenario,document,catalog,out var error)){message=error;return;}
   int index=UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath(scenario.scenePath);if(index<0){message="场景尚未加入构建列表";return;}
   Time.timeScale=1;AudioListener.volume=volume;loading=true;SceneManager.LoadSceneAsync(index);
  }
  private Vector3 Origin=>scenario.units.FirstOrDefault(u=>u.player)?.position??Vector3.zero;
  private MechScenarioStore.UnitEdit Edit(MechScenarioUnit unit)=>document.units.First(u=>u.key==unit.key);
  private void SaveScenario(){try{MechScenarioStore.Save(scenario,document);message="场景配置已保存";}catch(Exception e){message="保存失败："+e.Message;}}
  private IEnumerable<MechBattlefieldMap.Contact> ScenarioContacts()
  {
   int team=scenario.units.First(u=>u.player).team;
   foreach(var unit in scenario.units)yield return new MechBattlefieldMap.Contact{Key=unit.key,Name=unit.name,Type="MC",Own=unit.player,Position=Edit(unit).position,Confidence=1,Source="场景配置",Affiliation=unit.team==team?ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly:ACunicombat.Sensing.Contracts.TrackAffiliation.Hostile};
  }
  private Rect EditorPanel=>new Rect(Screen.width-300,20,280,440);
  private Rect EditorBottom=>new Rect(20,Screen.height-72,Screen.width-40,56);
  private void EnsureEditorMap()
  {
   if(editorMap==null){var host=new GameObject("Scenario Map");editorMap=host.AddComponent<MechBattlefieldMap>();
    editorMap.ConfigureScenarioEditor(Origin,ScenarioContacts,(key,pos,done)=>{var u=scenario.units.First(x=>x.key==key);if(done)SaveScenario();else{var edit=Edit(u);edit.position=new Vector3(pos.x,edit.position.y,pos.z);}},
     (key,context)=>{selectedUnit=scenario.units.FirstOrDefault(x=>x.key==key);unitMenu=context&&selectedUnit!=null;if(selectedUnit!=null)heightText=(Edit(selectedUnit).position.y/1000).ToString("0.0");var m=UnityEngine.InputSystem.Mouse.current.position.ReadValue();unitMenuPoint=new Vector2(Mathf.Min(m.x,Screen.width-160),Mathf.Min(Screen.height-m.y,Screen.height-100));},
     point=>EditorPanel.Contains(point)||EditorBottom.Contains(point)||(unitMenu&&new Rect(unitMenuPoint.x,unitMenuPoint.y,160,42).Contains(point)));
   }else if(!editorMap.Visible)editorMap.SetVisible(true);
  }
  private void DrawScenarioEditor()
  {
   EnsureEditorMap();int depth=GUI.depth;GUI.depth=-5000;
   GUI.Box(EditorPanel,"");float x=EditorPanel.x+14;
   GUI.Label(new Rect(x,36,250,36),"场景编辑",labelStyle);
   GUI.Label(new Rect(x,84,250,36),selectedUnit?.name??"选择单位",labelStyle);
   if(selectedUnit!=null)
   {
    var p=Edit(selectedUnit).position;GUI.Label(new Rect(x,130,250,64),$"X {p.x/1000:0.0} km / Z {p.z/1000:0.0} km",labelStyle);
    GUI.Label(new Rect(x,200,250,28),"高度（km）",labelStyle);heightText=GUI.TextField(new Rect(x,234,240,32),heightText,textStyle);
    if(Button(new Rect(x,280,240,38),"设置高度")&&float.TryParse(heightText,out var h)&&float.IsFinite(h)){Edit(selectedUnit).position=new Vector3(p.x,h*1000,p.z);SaveScenario();}
    if(Button(new Rect(x,330,240,38),"配装"))OpenEquipment(selectedUnit,Page.Editor);
   }
   if(unitMenu&&selectedUnit!=null&&Button(new Rect(unitMenuPoint.x,unitMenuPoint.y,160,42),"配装")){unitMenu=false;OpenEquipment(selectedUnit,Page.Editor);}
   GUI.Box(EditorBottom,"");
   if(Button(new Rect(30,Screen.height-62,130,36),"返回场景")){SaveScenario();editorMap.SetVisible(false);page=Page.Scenes;}
   if(Button(new Rect(174,Screen.height-62,160,36),"恢复默认位置")){foreach(var u in scenario.units){Edit(u).position=u.position;Edit(u).rotation=u.rotation;}SaveScenario();}
   GUI.Label(new Rect(350,Screen.height-62,Screen.width-550,38),"左键拖动单位 · 右键配装 · 中键平移 · 滚轮缩放",smallStyle);
   if(Button(new Rect(Screen.width-170,Screen.height-62,140,36),"开始场景"))StartBattle();
   GUI.depth=depth;
  }
  private void OpenEquipment(MechScenarioUnit unit,Page returnPage)
  {
   try
   {
    if(editorMap!=null)editorMap.SetVisible(false);equipmentUnit=unit;equipmentReturn=returnPage;
    if(draft!=null)Destroy(draft);
    if(unit.player)draft=Instantiate(MechLoadoutSelectionStore.ResolveSavedPlayerLoadout(unit.loadout));
    else draft=MechScenarioStore.BuildLoadout(unit,Edit(unit),catalog);
    hangarHost=new GameObject("Scenario Hangar");hangarHost.SetActive(false);
    var assembler=hangarHost.AddComponent<MechAssembler>();assembler.Loadout=draft;
    var menu=hangarHost.AddComponent<MechAssemblyMenu>();hangarHost.SetActive(true);
    menu.OpenForEditing(draft,SaveEquipment,()=>{page=equipmentReturn;Destroy(hangarHost);hangarHost=null;});page=Page.Equipment;
   }catch(Exception e){message=e.Message;page=returnPage;}
  }
  private void SaveEquipment(MechLoadoutDefinition value)
  {
   if(equipmentUnit.player)
   {
    if(value.Platform!=null)MechLayoutSelection.Save(value);
    else{var parts=new Dictionary<MechPartSlot,string>();var variants=new Dictionary<MechPartSlot,string>();foreach(var a in value.EnumerateEnabledAssignments()){parts[a.slot]=a.part.PartId;variants[a.slot]=a.missileVariantId;}MechLoadoutSelectionStore.SaveSelection(parts);MechLoadoutSelectionStore.SaveMissileVariants(parts,variants);}
   }
   else{MechScenarioStore.StoreLoadout(Edit(equipmentUnit),value);MechScenarioStore.Save(scenario,document);}
   message="配装已保存";
  }
  private void DrawSettings()
  {
   float x=(Width-520)/2;GUI.Label(new Rect(x,200,520,30),"主音量 "+Mathf.RoundToInt(volume*100)+"%",labelStyle);volume=GUI.HorizontalSlider(new Rect(x,244,520,25),volume,0,1);AudioListener.volume=volume;
   GUI.Label(new Rect(x,300,520,30),"画质",labelStyle);quality=GUI.SelectionGrid(new Rect(x,342,520,80),Mathf.Clamp(quality,0,QualitySettings.names.Length-1),QualitySettings.names,3,buttonStyle);
   fullscreen=GUI.Toggle(new Rect(x,460,520,30),fullscreen,"全屏");
   if(Button(new Rect(x,550,180,44),"保存并返回")){PlayerPrefs.SetFloat("frontend.volume",volume);PlayerPrefs.SetInt("frontend.quality",quality);PlayerPrefs.SetInt("frontend.fullscreen",fullscreen?1:0);PlayerPrefs.Save();QualitySettings.SetQualityLevel(quality,true);if(!Application.isEditor)Screen.fullScreen=fullscreen;page=Page.Home;message="设置已保存";}
  }
  private void OnDestroy(){if(editorMap!=null)Destroy(editorMap.gameObject);if(hangarHost!=null)Destroy(hangarHost);if(draft!=null)Destroy(draft);if(font!=null)Destroy(font);}
 }
}
