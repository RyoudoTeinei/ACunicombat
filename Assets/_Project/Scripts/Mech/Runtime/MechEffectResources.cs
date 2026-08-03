using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 程序化特效（真空羽焰、RCS 喷口、受击火光与碎片）共用的运行时资源工厂。
    ///
    /// 存在的理由：URP 的 Particles/Unlit 默认是 <b>不透明</b>（`_Surface=0`、`_SrcBlend=One`、
    /// `_DstBlend=Zero`、`_ZWrite=1`、RenderType=Opaque）。只设 `_BaseColor` 而不改这套混合状态，
    /// 得到的是写深度的实心方片而不是发光羽焰——这正是修复前 RCS 喷口的表现。把配方集中在这里，
    /// 新增特效就不会再各自踩一遍。
    ///
    /// 与 FFX 栈（<c>AC6FxrEffectFactory</c> 从 AC6 原始数据抽取、`Tools/LocalOut` 与
    /// `Generated/` 均在 .gitignore 内）不同，本工厂全部为程序化构建，可提交、可复现。
    /// 按 `01_待开发事项/画面与渲染升级计划.md` 阶段 5 的美术边界：推进器与羽焰类效果走这条路径。
    /// </summary>
    public static class MechEffectResources
    {
        private static Texture2D softParticleTexture;
        private static Mesh debrisShardMesh;

        /// <summary>径向柔化的白色圆点。所有加色粒子共用，避免出现硬边方片。</summary>
        public static Texture2D SoftParticleTexture
        {
            get
            {
                if (softParticleTexture == null)
                {
                    softParticleTexture = BuildSoftParticleTexture();
                }

                return softParticleTexture;
            }
        }

        /// <summary>低面数不规则四面体碎片。自转时各面亮度不同，这个闪烁本身就是"金属碎片"的读法。</summary>
        public static Mesh DebrisShardMesh
        {
            get
            {
                if (debrisShardMesh == null)
                {
                    debrisShardMesh = BuildDebrisShardMesh();
                }

                return debrisShardMesh;
            }
        }

        /// <summary>
        /// URP 加色（Additive）透明粒子材质。<paramref name="color"/> 可超过 1 以推过 Bloom 阈值
        /// （战斗 Volume 当前为 `1.1`）——达不到阈值就不会泛光，也不会喂到 ScreenSpaceLensFlare。
        /// </summary>
        public static Material CreateAdditiveParticleMaterial(string materialName, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                name = materialName,
                hideFlags = HideFlags.DontSave,
            };

            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.One);
            material.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            material.SetInt("_DstBlendAlpha", (int)BlendMode.One);
            material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_Cull"))
            {
                material.SetInt("_Cull", (int)CullMode.Off);
            }

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.SetShaderPassEnabled("DepthOnly", false);
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");

            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", SoftParticleTexture);
            }
            material.mainTexture = SoftParticleTexture;
            SetColor(material, color);
            return material;
        }

        /// <summary>
        /// 加色拖尾材质。与粒子材质的区别是<b>不贴软圆点贴图</b>——那张贴图沿拖尾拉伸后
        /// 会在长度方向糊成一条渐变，反而破坏曳光弹应有的均匀亮度；拖尾的形状由
        /// <c>TrailRenderer</c> 的宽度曲线和颜色渐变控制。
        /// </summary>
        public static Material CreateAdditiveTrailMaterial(string materialName, Color color)
        {
            var material = CreateAdditiveParticleMaterial(materialName, color);
            if (material == null)
            {
                return null;
            }

            material.name = materialName;
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", null);
            }
            material.mainTexture = null;
            return material;
        }

        /// <summary>受主光照明的不透明粒子材质，用于碎片——它靠反光而不是自发光被看见。</summary>
        public static Material CreateLitDebrisMaterial(string materialName, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                name = materialName,
                hideFlags = HideFlags.DontSave,
            };

            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetInt("_SrcBlend", (int)BlendMode.One);
            material.SetInt("_DstBlend", (int)BlendMode.Zero);
            material.SetInt("_ZWrite", 1);
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Geometry;
            material.SetOverrideTag("RenderType", "Opaque");
            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0.72f);
            }
            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.42f);
            }

            SetColor(material, color);
            return material;
        }

        public static void SetColor(Material material, Color color)
        {
            if (material == null)
            {
                return;
            }

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }

        /// <summary>
        /// 真空羽焰/受击火光共用的黑体冷却渐变：白 → 黄白 → 暗橙。
        /// 真空里没有氧化剂，所谓"火光"是白热碎屑与金属蒸气的黑体辐射，
        /// 因此是高色温向低色温衰减，而不是大气火焰的橙红。
        /// </summary>
        public static Gradient CreateBlackbodyCoolingGradient(float startAlpha = 1f)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.98f, 0.94f), 0f),
                    new GradientColorKey(new Color(1f, 0.88f, 0.62f), 0.35f),
                    new GradientColorKey(new Color(0.95f, 0.55f, 0.22f), 0.72f),
                    new GradientColorKey(new Color(0.42f, 0.13f, 0.04f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(startAlpha, 0f),
                    new GradientAlphaKey(startAlpha * 0.85f, 0.4f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        /// <summary>把粒子渲染器统一配成不投影、不接收阴影、不采样探针的自发光形态。</summary>
        public static void ConfigureUnlitRenderer(ParticleSystemRenderer renderer, Material material)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private static Texture2D BuildSoftParticleTexture()
        {
            const int resolution = 64;
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true)
            {
                name = "AC6 Effect Soft Particle (Runtime)",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };

            var pixels = new Color32[resolution * resolution];
            for (var y = 0; y < resolution; y++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    var uv = new Vector2(
                        (x + 0.5f) / resolution * 2f - 1f,
                        (y + 0.5f) / resolution * 2f - 1f);
                    var radial = Mathf.Clamp01(1f - uv.magnitude);
                    var alpha = radial * radial * (3f - 2f * radial);
                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Mesh BuildDebrisShardMesh()
        {
            var mesh = new Mesh
            {
                name = "AC6 Effect Debris Shard (Runtime)",
                hideFlags = HideFlags.DontSave,
                vertices = new[]
                {
                    new Vector3(-0.52f, -0.2f, -0.28f),
                    new Vector3(0.58f, -0.14f, -0.24f),
                    new Vector3(0.16f, 0.46f, -0.06f),
                    new Vector3(-0.08f, 0.06f, 0.5f),
                },
                triangles = new[]
                {
                    0, 2, 1,
                    0, 1, 3,
                    1, 2, 3,
                    2, 0, 3,
                },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
