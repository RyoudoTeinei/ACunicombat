using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Editor-only design preview. Nothing is attached to the gameplay HUD or damage pipeline.
public sealed class MechDamageOutlinePreview : EditorWindow
{
    private MechLoadoutDefinition loadout;
    private Texture2D outline;
    private float yaw;
    public enum PreviewDamageState { NormalOrLight, FunctionImpaired, SevereButUsable, Destroyed }
    private PreviewDamageState damageExample;
    [MenuItem("Tools/ACunicombat/Mech/Damage Outline Preview")]
    public static void Open() => GetWindow<MechDamageOutlinePreview>("Damage Outline");
    private void OnDisable() { if(outline!=null) DestroyImmediate(outline); }
    private void OnGUI()
    {
        EditorGUI.BeginChangeCheck();
        loadout=(MechLoadoutDefinition)EditorGUILayout.ObjectField("Loadout",loadout,typeof(MechLoadoutDefinition),false);
        yaw=EditorGUILayout.Slider("View angle",yaw,-180,180);
        damageExample=(PreviewDamageState)EditorGUILayout.EnumPopup("Example equipment state",damageExample);
        bool changed=EditorGUI.EndChangeCheck();
        if((changed || GUILayout.Button("Rebuild from equipped meshes")) && loadout!=null)
        { if(outline!=null)DestroyImmediate(outline); outline=Render(loadout,512,yaw,damageExample); }
        var rect=GUILayoutUtility.GetRect(100,10000,100,10000);
        EditorGUI.DrawRect(rect,new Color(.025f,.05f,.065f));
        if(outline!=null)GUI.DrawTexture(rect,outline,ScaleMode.ScaleToFit,true);
    }
    [Serializable] private sealed class PartConfig { public string slot,assetPath; }
    [Serializable] private sealed class PlatformConfig { public string name; public PartConfig[] parts; }
    [Serializable] private sealed class Config { public PlatformConfig[] platforms; }
    public static void ExportCurrentPreview()
    {
        string configPath=Environment.GetEnvironmentVariable("ACUNICOMBAT_OUTLINE_CONFIG");
        string output=Environment.GetEnvironmentVariable("ACUNICOMBAT_OUTLINE_OUTPUT");
        if(string.IsNullOrEmpty(configPath)||string.IsNullOrEmpty(output))throw new InvalidOperationException("Preview config/output environment variables are required.");
        Directory.CreateDirectory(output);
        var config=JsonUtility.FromJson<Config>(File.ReadAllText(configPath));
        var platform=config.platforms.First(p=>p.name.StartsWith("Player",StringComparison.Ordinal));
        var assignments=new List<MechPartAssignment>();
        foreach(var entry in platform.parts)
        {
            var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(entry.assetPath);
            if(part==null)throw new InvalidOperationException("Missing actual equipped asset: "+entry.assetPath);
            assignments.Add(new MechPartAssignment {slot=(MechPartSlot)Enum.Parse(typeof(MechPartSlot),entry.slot),part=part});
        }
        var current=CreateInstance<MechLoadoutDefinition>();
        var images=new List<Texture2D>();
        try
        {
            current.ConfigureRuntime("outline-preview","Current equipment",assignments);
            foreach(PreviewDamageState state in Enum.GetValues(typeof(PreviewDamageState)))images.Add(Render(current,512,0,state));
            string[] names={"normal-or-light.png","function-impaired.png","severe-but-usable.png","destroyed.png"};
            for(int i=0;i<images.Count;i++)File.WriteAllBytes(Path.Combine(output,names[i]),images[i].EncodeToPNG());
            var sheet=new Texture2D(2048,576,TextureFormat.RGBA32,false,true);
            try
            {
                var pixels=Enumerable.Repeat(new Color32(8,17,23,255),2048*576).ToArray();
                for(int panel=0;panel<4;panel++)
                {
                    var source=images[panel].GetPixels32();
                    for(int y=0;y<512;y++)for(int x=0;x<512;x++)
                    {
                        var c=source[y*512+x];int index=(y+32)*2048+panel*512+x;
                        float a=c.a/255f;var bg=pixels[index];
                        pixels[index]=new Color32((byte)(c.r*a+bg.r*(1-a)),(byte)(c.g*a+bg.g*(1-a)),(byte)(c.b*a+bg.b*(1-a)),255);
                    }
                }
                sheet.SetPixels32(pixels);sheet.Apply();File.WriteAllBytes(Path.Combine(output,"comparison.png"),sheet.EncodeToPNG());
            }
            finally {DestroyImmediate(sheet);}
            File.WriteAllText(Path.Combine(output,"preview.html"),"<!doctype html><meta charset='utf-8'><title>四档部件状态预览</title><style>body{background:#081117;color:#c2ddd6;font:16px system-ui;margin:24px}main{display:grid;grid-template-columns:repeat(4,minmax(230px,1fr));gap:12px}section{text-align:center}img{width:100%;max-width:512px}p{line-height:1.8}h3{font-size:17px}</style><h2>相同配装、相同部件的四档状态</h2><p>对比左上角同一件装备。所有装备始终保留；只有未安装时才不显示，完全损坏仍显示红色空心轮廓。</p><main><section><h3>绿色：正常 / 轻微受损</h3><p>功能不受影响 · 淡绿填充</p><img src='normal-or-light.png'></section><section><h3>黄色：受损</h3><p>开始影响功能 · 淡黄填充</p><img src='function-impaired.png'></section><section><h3>红色：严重受损</h3><p>仍可使用 · 淡红填充</p><img src='severe-but-usable.png'></section><section><h3>红框空心：完全损坏</h3><p>功能失效 · 内部透明</p><img src='destroyed.png'></section></main>");
            Debug.Log("[DamageOutlinePreview] Exported actual-model outlines to "+output);
        }
        finally {foreach(var texture in images)DestroyImmediate(texture);DestroyImmediate(current);}
    }
    public static Texture2D Render(MechLoadoutDefinition loadout,int resolution,float yaw,PreviewDamageState damageState)
    {
        using(var diagram=MechDamageDiagramRenderer.Bake(loadout,resolution,yaw))
        {
            diagram.Recolor(id=>id==MechSlotGraph.LegacyId(MechPartSlot.RightBackWeapon)?(MechPartCondition)damageState:MechPartCondition.NormalOrLight);
            var texture=new Texture2D(resolution,resolution,TextureFormat.RGBA32,false,true);
            texture.SetPixels32(diagram.Texture.GetPixels32());texture.Apply();return texture;
        }
    }
}
