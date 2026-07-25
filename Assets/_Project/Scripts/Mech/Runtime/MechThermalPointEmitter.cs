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

        private void OnDisable() => registry.Remove(this);

        private void Update()
        {
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

                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetFloat(ThermalTempId, temperatureK);
                renderer.SetPropertyBlock(propertyBlock);
            }

            applied = true;
        }
    }
}
