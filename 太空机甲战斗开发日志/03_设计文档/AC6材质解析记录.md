# AC6 材质解析记录

## 当前结论

- 素材目录内没有独立 `.matxml` 文件，但 `.flver/.fbx` 字符串里保留了材质名和 matxml 引用。
- 典型核心材质名类似：
  - `CORE_mat|ACparts[BD_M_1010][CM].matxml|0`
  - `CORE_Lmiter_mat|ACparts[BD_M_1010]_Limiter.matxml|1`
  - `EYE_CORE_mat|ACparts[BD_M_1010]_Emissive.matxml|2`
  - `Prats_Making_mat|ACparts[Prats_Marking].matxml|3`
- `CM` 基本可判定为 Color Material 主装甲材质；`Limiter` 和 `Emissive` 应作为发光材质；`Marking` 是标记/贴花类材质。
- FLVER 字符串里还能看到原 shader 参数名，例如：
  - `GSBlendMap_AlbedoMap`
  - `GSBlendMap_NormalMap`
  - `GSBlendMap_Mask3Map`
  - `GSBlendMap_Mask1Map`
  - `GSBlendMap_EmissiveMap`
- 部件目录里的主贴图规则：
  - `*_01_a.dds`：主 albedo/base
  - `*_01_n.dds`：主 normal
  - `*_01_3m.dds` 或 `*_01_m.dds`：原 shader mask，不等同于 Unity MetallicGlossMap
  - `*_01_em.dds`：发光
  - `ac_01_a_u / ac_01_n_u / ac_01_r_u`：颜色材质/涂装层近似资源
  - `ac_02_a_u / ac_03_a_u`：额外涂装/材质层资源

## 已接入的近似规则

- Unity 导入器现在按 FBX 原始材质名生成多个材质，不再把所有子 mesh 强行套同一个材质。
- 装配器按原始材质名匹配材质覆盖，避免多个子 mesh 的第 0 槽全部被同一个材质替换。
- 新增 `ACunicombat/AC6/Parts Approx` shader：
  - 使用 URP PBR 光照。
  - 接入 base、normal、3m mask、emission。
  - `_3m` 只做受控的金属度、粗糙度、遮蔽近似。
  - `Limiter / Emissive` 材质会启用自发光。
  - `CM` 材质使用稳定主色，原 `_a` 和 `_3m` 只提供很弱的细节变化，避免把非最终贴图直接显示成花斑。

## 当前问题修正

- 直接把 `*_01_a.dds` 当最终 albedo 会产生大面积灰黑斑块；当前 shader 已改为只提取弱明暗细节。
- 直接用 vertex color 做涂装权重也会造成异常斑驳；当前近似 shader 不再用 vertex color 控制颜色。
- `_3m` 对遮蔽的影响已显著降低，避免 mask 通道把整机压黑。

## Blender 参考结论

- 已用 `D:/steam/steamapps/common/Blender/blender.exe` 读取 `E:/UnityDev/Resources/AC6AllFramePartsPacked.blend`，确认其中保留了打包后的 AC6 frame parts 材质节点。
- `CM` 主装甲材质不是简单 PBR，也不是直接把 `*_01_a` 当最终底色。典型材质如 `CORE_mat|ACparts[BD_M_1010][CM].matxml|0` 会把 `3m` mask、normal 的部分通道和 `Zone 1-7` 颜色参数送入一个总材质组。
- `3m.R` 经过 `CONSTANT` ColorRamp 生成 `Mask_0`：
  - `< 0.068`：不选区，保持 Zone 1。
  - `0.068 - 0.182`：`Mask_0.R`，选择 Zone 2。
  - `0.182 - 0.332`：`Mask_0.G`，半强度混合 Zone 3。
  - `>= 0.332`：`Mask_0.B`，选择 Zone 4。
- `Mask_1` 目前按 Blender 节点近似：
  - `3m.R` 在 `0.055 - 0.091` 的窄带选择 Zone 5。
  - 反相 `3m.G` 接近 1 时选择 Zone 6。
  - Zone 7 更像 dust/dark 叠层，暂未完整还原。
- 当前 Unity shader 已增加 AC6 Zone 模式：CM 材质使用 Zone 颜色和离散 mask 选择，原 `_a` 只保留很弱的面板明暗细节；Limiter、Emissive、Marking 不走 Zone 模式。
- 已建立另一条测试通道：直接从 `AC6AllFramePartsPacked.blend` 选取 MELANDER 1010 的头、核心、手臂、腿，把 Blender 节点组的最终 `Color` 输出临时接到 Emission 后烘焙为 PNG，再导出 FBX 给 Unity。这样 Unity 侧不需要理解 Blender 复杂节点，可以先验证“Blender 已解析结果”在游戏内的观感。
- 直接烘 `DIFFUSE` 会因为金属/复杂 BSDF 关系得到偏黑贴图；当前导出脚本改用 `EMIT` 烘焙基础色。
- 已建立武器的原始素材烘焙试验管线：从 `E:/UnityDev/Resources/AC6_ExtractedAssets/wp` 导入原始武器 FBX，在 Blender 中按同目录 `*_a / *_3m / *_m` DDS 重建一个近似预览材质，再用 `EMIT` 烘焙为 PNG 并导出 Unity FBX。首批样本包含 3 把手持武器和 3 个背部武器。
- 武器管线与 frame pack 烘焙不同：武器不是从已整理好的 `.blend` 直接烘结果，而是从 extracted 原始 FBX/DDS 重建一个近似材质后再烘。这证明“原始素材 -> Blender 处理 -> Unity baked 资源”这条中期路线可以跑通。

## 待继续研究

- 反推 `_3m` 各通道的准确含义。
- 反推 `Mask1Map` 来源和作用，目前导出目录没有明显的 `mask1` 独立贴图。
- 研究 vertex color 与多 UV 通道在原 shader 中是否用于涂装、损伤或 decal 混合。
- 若要更接近原效果，需要继续做自定义 shader，将 `ac_01/02/03` 作为真正的颜色材质层而不是简单 tint。
- 如果烘焙测试版观感明显更好，可以考虑把它作为临时美术导入管线；缺点是涂装不可在游戏内实时换色，且每个部件会产生额外贴图。
- 武器目前只烘基础色/发光近似，法线、粗糙度、金属度还没有独立烘焙；后续如果武器在近景下不够立体，应补 normal/roughness/metallic 导出。
