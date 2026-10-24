using UnityEngine;
using UnityEngine.Rendering;
namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechDamageDiagramHud : MonoBehaviour
    {
        private MechPartDamageController damage;
        private MechRuntimeState runtime;
        private MechDamageDiagramRenderer diagram;
        private int layoutVersion=-1,damageVersion=-1;
        public Texture2D DiagramTexture=>diagram?.Texture;
        private void LateUpdate()
        {
            if(GetComponent<MechPlayerRig>()==null||GetComponent<MechAiTargetController>()!=null||SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)return;
            if(damage==null)damage=GetComponent<MechPartDamageController>();if(runtime==null)runtime=GetComponent<MechRuntimeState>();
            if(damage==null||!damage.IsBound||runtime?.CurrentLoadout==null)return;
            if(diagram==null||layoutVersion!=damage.LayoutVersion)
            {
                diagram?.Dispose();diagram=MechDamageDiagramRenderer.Bake(runtime.CurrentLoadout,256);
                layoutVersion=damage.LayoutVersion;damageVersion=-1;
            }
            if(damageVersion!=damage.Version){diagram.Recolor(damage.GetCondition);damageVersion=damage.Version;}
        }
        private void OnGUI()
        {
            if(diagram?.Texture==null||GetComponent<MechAiTargetController>()!=null||MechSolarSystemMap.IsAnyMapVisible||MechAssemblyMenu.IsAnyMenuVisible)return;
            var previous=MechHudLayout.BeginScaledGui();var color=GUI.color;
            GUI.color=Color.white;GUI.DrawTexture(MechHudLayout.OwnshipDiagramRect,diagram.Texture,ScaleMode.ScaleToFit,true);
            int supportLimited=0;
            foreach(var region in diagram.RegionIds)if(damage.IsSupportLimited(region))supportLimited++;
            if(supportLimited>0)
            {
                GUI.color=new Color(1f,.73f,.12f);
                var rect=MechHudLayout.OwnshipDefenseStatusRect;rect.y-=24f;
                GUI.Label(rect,$"支撑受损：{supportLimited} 个部件效能受限");
            }
            GUI.color=color;GUI.matrix=previous;
        }
        private void OnDestroy(){diagram?.Dispose();}
    }
}
