using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 恒温红外点源：给没有完整热模型的物体（试验目标机、导弹、残骸）一个
    /// 可见的热特征。启动时把 _ThermalTemp 写进子级 renderer 的 MPB，
    /// 之后仅在 SetTemperature 时重推。MechThermalVisionController 会在热视野
    /// 开启期间给未标记的 CombatTestTarget / MechTestProjectile 自动补挂本组件。
    /// </summary>
    public sealed class MechThermalPointEmitter : MonoBehaviour
    {
        private static readonly List<MechThermalPointEmitter> registry = new();
        public static IReadOnlyList<MechThermalPointEmitter> Registry => registry;

        private static readonly int ThermalTempId = Shader.PropertyToID("_ThermalTemp");

        [SerializeField] private float temperatureK = 330f;

        private MaterialPropertyBlock propertyBlock;
        private readonly Dictionary<Renderer, MaterialPropertyBlock>
            rendererPropertyBlockBackups = new();
        private bool applied;

        public float TemperatureK => temperatureK;

        public void SetTemperature(float kelvin)
        {
            temperatureK = kelvin;
            applied = false;
        }

        private void OnEnable()
        {
            registry.Add(this);
            applied = false;
        }

        private void OnDisable()
        {
            registry.Remove(this);
            if (applied)
            {
                ClearTemperatureOverrides();
                applied = false;
            }
        }

        private void Update()
        {
            if (!MechThermalSignature.RendererTemperatureOutputEnabled)
            {
                if (applied)
                {
                    ClearTemperatureOverrides();
                    applied = false;
                }
                return;
            }

            if (applied)
            {
                return;
            }

            propertyBlock ??= new MaterialPropertyBlock();
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                {
                    continue;
                }
                if (!rendererPropertyBlockBackups.ContainsKey(renderer))
                {
                    var backup = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(backup);
                    rendererPropertyBlockBackups.Add(
                        renderer,
                        backup.isEmpty ? null : backup);
                }

                propertyBlock.Clear();
                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetFloat(ThermalTempId, temperatureK);
                renderer.SetPropertyBlock(propertyBlock);
            }

            applied = true;
        }

        private void ClearTemperatureOverrides()
        {
            foreach (var pair in rendererPropertyBlockBackups)
            {
                if (pair.Key != null)
                {
                    pair.Key.SetPropertyBlock(pair.Value);
                }
            }

            rendererPropertyBlockBackups.Clear();
        }
    }
}
