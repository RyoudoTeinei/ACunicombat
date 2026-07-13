# AC6 资源导入难点汇总（咨询用）

日期：2026-07-09

## 背景

项目是 Unity 6.5 本地实验工程，路径：

`E:\UnityDev\ACunicombat`

目标不是复刻或发布原游戏资源，而是研究一条能服务自制太空机甲游戏的资源/动画管线。当前希望搞清楚：

- AC6 机体模型如何以正确外观进入 Unity。
- AC6 原始骨架、蒙皮权重、HKX 动画能否保持同一套 bind pose / rest pose。
- 是否可以避免“丢掉骨架再重建骨架”的权宜做法。

相关资源路径：

- 原始/解包资源：`E:\UnityDev\Resources\AC6_ExtractedAssets`
- 已整理的 Blender 文件：`E:\UnityDev\Resources\AC6AllFramePartsPacked.blend`
- Blender：`D:\steam\steamapps\common\Blender\blender.exe`

## 当前结论

目前卡点不是单一问题，而是三条管线没有合成一条可靠管线：

- **视觉正确管线**：Blender 能把 AC6 材质节点/贴图规则解释成可见结果，烘焙成 PNG 后 Unity 显示正常。
- **骨架/动画正确管线**：HKX 动画可以解析成骨骼轨道，Unity 可以生成 `AnimationClip`，但要求模型骨架与 HKX reference pose 完全一致。
- **换装系统管线**：项目已有模块化部件装配系统，适合游戏内自由换装，但它天然更像“部件挂点 + 程序动画”，不等价于原版完整 skinned rig。

现在的问题是：视觉正确的 `.blend`/baked 部件和 HKX 动画使用的参考骨架空间不一致；而直接取原始资源进 Unity，又会因为 Unity 不理解 AC6 材质系统而变成灰模/近似灰模。

## 已经跑通的内容

### 1. 武器 bake 管线基本成功

脚本：

`E:\UnityDev\ACunicombat\Tools\Blender\ExportAC6ExtractedWeaponBakes.py`

Unity 导入器：

`E:\UnityDev\ACunicombat\Assets\_Project\Editor\AC6RawBakedWeaponImporter.cs`

流程：

`AC6_ExtractedAssets` 中的武器 FBX/DDS  
→ Blender 中重建近似材质  
→ 用 `EMIT` bake 成普通 PNG  
→ 导出 FBX  
→ Unity 使用普通材质显示

观察：

- 武器在 Unity 中视觉基本正常。
- 这条管线成功的关键是：武器大多不涉及复杂角色骨架/蒙皮动画。
- Unity 并没有真正理解 AC6 shader；只是显示了 Blender 预先 bake 好的普通贴图。

### 2. Melander frame parts 静态烘焙成功

脚本：

`E:\UnityDev\ACunicombat\Tools\Blender\ExportAC6BlendBakedSamples.py`

Unity 导入器：

`E:\UnityDev\ACunicombat\Assets\_Project\Editor\AC6BlendBakedPartsImporter.cs`

输出：

`E:\UnityDev\ACunicombat\Assets\_Project\ImportedFromBlend\MELANDER_1010`

流程：

`AC6AllFramePartsPacked.blend` 中的 MELANDER 1010 部件  
→ Blender 使用已有材质节点输出  
→ bake 为 PNG  
→ Unity 生成 `MechPartDefinition`  
→ 当前换装系统可装配

观察：

- 静态/模块化视觉效果明显好于 Unity 直接解析原始 AC6 材质。
- 但这条管线主要服务“部件显示和换装”，不是原版完整 skinned rig 动画。

### 3. HKX 动画数据已能进入 Unity 预览

相关脚本/导入器：

- `E:\UnityDev\ACunicombat\Assets\_Project\Editor\AC6OriginalHkxAnimationImporter.cs`
- `E:\UnityDev\ACunicombat\Assets\_Project\Editor\AC6UnifiedSkinnedHkxPreviewImporter.cs`
- `E:\UnityDev\ACunicombat\Tools\Blender\ExportAC6HkxRestMelanderSkinned.py`
- `E:\UnityDev\ACunicombat\Tools\Blender\DiagnoseAC6FbxHkxRigSpace.py`

输出示例：

`E:\UnityDev\ACunicombat\Assets\_Project\ImportedFromGame\Animations\C0000_A000_010900`

当前验证数据：

- HKX reference pose 骨骼数：130
- FBX roundtrip 检查骨骼：130
- 缺失骨骼：0
- 旋转误差 > 1 度：0
- 位置误差 > 0.001：0

观察：

- 蓝点骨架预览可以播放原 HKX 动作。
- 生成的 HKX-rest skinned 模型也能站立并播放，但视觉仍偏灰，且这条管线用了“迁移/重建权重”的实验做法，不适合作为最终管线。

## 遇到的主要难点

### 1. Unity 不理解 AC6 材质系统

AC6 材质不是简单的 Unity Lit/PBR：

- 材质名里保留了 `matxml` 引用，例如：
  - `CORE_mat|ACparts[BD_M_1010][CM].matxml|0`
  - `CORE_Lmiter_mat|ACparts[BD_M_1010]_Limiter.matxml|1`
  - `EYE_CORE_mat|ACparts[BD_M_1010]_Emissive.matxml|2`
  - `Prats_Making_mat|ACparts[Prats_Marking].matxml|3`
- 贴图名包括：
  - `*_a.dds`
  - `*_n.dds`
  - `*_3m.dds` 或 `*_m.dds`
  - `*_em.dds`
  - `ac_01_a_u / ac_01_n_u / ac_01_r_u`
  - `ac_02_a_u / ac_03_a_u`
- Blender 节点显示 `CM` 主装甲材质会使用 Zone 颜色、mask、normal、3m、多层颜色材质混合。

Unity 直接导入 FBX/DDS 时，即使 UV 和贴图存在，也不知道：

- 哪个通道是颜色遮罩。
- 哪个通道是 roughness/metallic/ambient occlusion。
- Zone 1-7 颜色如何映射。
- 发光、Limiter、Marking 如何组合。

结果就是主体装甲容易变成灰模/近似灰模；紫色发光或贴花有时能显示，是因为那部分材质逻辑更简单。

### 2. Blender bake 能解决视觉，但不自动解决骨架

武器和静态部件能正常显示，是因为 Blender 先把复杂材质 bake 成普通 PNG。Unity 只负责显示普通贴图。

但对机体角色来说，不能只看视觉，还必须保留：

- 原始骨架层级。
- 每根骨骼的 bind pose/rest pose。
- SkinnedMeshRenderer bindposes。
- 顶点权重。
- HKX 动画使用的 reference pose。

如果 bake 流程把模型转成静态部件，或者在 Blender 中复制/重建 mesh 时破坏 bindpose，Unity 动画就会散架、漂移或横躺。

### 3. “骨骼名字一样”不等于能套动画

已经验证过：把 HKX 动画曲线按骨骼名直接套到 `.blend` 中视觉正确的 Melander 部件上，会出现各部件漂移/散架。

原因是：

- `.blend` 中的部件骨架和 HKX 动画参考骨架不是同一套本地空间。
- 父子层级、局部 rest pose、附加挂点骨/辅助骨不完全一致。
- Unity 的 skinned animation 依赖 bindpose 矩阵，不只是骨骼名。

因此不能简单做“按名字 retarget”。

### 4. HKX 动画导入仍不是完整商业级管线

已调研 DSAnimStudio 5.0-RC6.1：

- release note 和源码里有 AC6 支持痕迹。
- 可以作为 AC6 动作读取/预览工具。
- 但未发现直接导出 Unity 可用 FBX/glTF 动画的一键路径。
- 其导出更偏 Havok XML/Packfile/Tagfile，需要进一步解析后生成 Unity `AnimationClip`。

当前我们能从 HKX 得到骨骼 local transform 轨道，并生成 Unity `AnimationClip`，但最终还需要确认：

- HKX track 的 reference pose 是否与某个原始 character FBX 完全一致。
- 变换矩阵的坐标系、轴向、缩放、四元数 handedness 是否全部正确。
- TAE/BHV 中是否还有动作事件、root motion、状态切换、IK/辅助轨道。

### 5. 模块化换装和完整 skinned rig 存在架构冲突

项目希望做类似 AC6 的自由换装：

- 头
- 核心
- 手臂
- 腿
- booster
- generator
- FCS
- 左右手武器
- 左右背武器

当前换装系统使用 `MechPartDefinition`、挂点和装配器，适合游戏逻辑。

但原版角色动画更像是完整 skeleton 驱动。如果每个部件都要自由替换，必须决定：

- 所有部件是否都绑定到一套统一骨架。
- 不同腿型/手臂型是否共享同一 bind pose。
- 武器/背包/推进器是否用骨骼驱动，还是用挂点和程序动画驱动。
- 原 HKX 动画是否只是参考姿态，而不是最终运行时动画。

## 当前失败/不满意的路线

### 路线 A：直接 Unity 导入原始 AC6 FBX/DDS

结果：

- mesh 能出现。
- 部分发光/标记可见。
- 主体装甲接近灰模。

判断：

- Unity 不理解 matxml/shader/mask/Zone 规则。
- 需要自定义 shader 和更完整的材质解析。

### 路线 B：静态 baked 部件 + 直接套 HKX 动画

结果：

- 部件视觉好。
- 播放 HKX 后各部件漂移/散架。

判断：

- 视觉正确的部件骨架空间与 HKX reference pose 不一致。
- 骨骼名匹配不足以 retarget。

### 路线 C：重建 HKX-rest 骨架并迁移 mesh 权重

结果：

- 骨架 roundtrip 验证通过。
- 能站立并播放。
- 视觉仍偏灰。
- 权重迁移属于实验做法，不适合作为长期主线。

判断：

- 这证明 HKX 动画可以进 Unity。
- 但不应把“丢弃原骨架再重建”作为最终资产管线。

## 希望外部咨询者重点判断的问题

### 问题 1：AC6 原始 character/model 资源里，是否存在可直接匹配 HKX 动画的 skinned mesh？

需要确认：

- `chr/AC - c0000` 或其他角色目录中的模型，是否自带与 HKX 动画一致的 skeleton/bind pose。
- 如果有，推荐用什么工具链导出到 FBX/glTF，同时保留 bindpose 和 vertex weights。
- 是否需要从 FLVER 而不是已导出的 FBX 开始处理。

### 问题 2：DSAnimStudio 或其他 FromSoftware 工具，能否导出“Unity 可直接用”的 AC6 动画？

目前观察是 DSAnimStudio 更偏 Havok XML/Packfile/Tagfile，而不是 FBX 动画。

想确认：

- 有没有社区已验证的 AC6 HKX → FBX/glTF/Unity AnimationClip 管线。
- 如果只能导出 Havok XML，是否有成熟解析器可还原到 Unity `AnimationClip`。
- AC6 的 HKX local transform 是否需要特殊坐标系转换。

### 问题 3：怎样在 Blender 中 bake 材质，但不破坏原 skinned mesh 的 bind pose？

理想流程是：

`原始 skinned mesh + 原始骨架 + 原始权重`  
→ Blender 中只 bake 材质输出  
→ 保持骨架、权重、bindpose 不变  
→ 导出 Unity skinned FBX

需要确认：

- Blender 复制/导出 skinned mesh 时哪些操作会改变 bindpose。
- 是否应该完全避免 duplicate/apply transform/apply modifier。
- FBX 导出设置如何避免轴转换和 bindpose 改写。

### 问题 4：AC6 材质系统是否已有公开解析规则？

当前只做了近似：

- `*_a` 不等于最终 albedo。
- `*_3m` / `*_m` 是 mask，不等同 Unity MetallicGlossMap。
- `CM` 主装甲依赖 Zone 颜色和 mask。
- `Limiter / Emissive / Marking` 有单独逻辑。

希望确认：

- `3m` 各通道准确含义。
- `Mask1Map` 来源和作用。
- `ac_01/02/03` 这些通用涂装纹理如何参与混合。
- 是否可以在 Unity ShaderGraph/HLSL 中实时复刻，而不是 bake。

### 问题 5：对游戏开发而言，是否值得追求原 HKX 动画？

项目是太空机甲战斗，最终运动逻辑会包含：

- 惯性滑行。
- 反推刹车。
- 姿态控制。
- 武器瞄准。
- 推进器喷口。
- 相对速度导弹火控。

因此原 AC6 地面机甲动作可能只适合做姿态参考。希望判断：

- 是否应放弃完整原 HKX 动作，只保留 baked/模块化部件和自制程序动画。
- 或者保留原 skeleton 作为基础，重新做自定义动画层。

## 当前建议路线

如果目标是长期稳定开发，而不是继续试验，建议优先考虑：

1. **停止把“重建骨架”作为主线**  
   它只适合验证 HKX 动画，不适合最终资产。

2. **寻找/确认原始 skinned mesh 与 HKX 动画的真实对应关系**  
   如果能找到同一套 skeleton/bindpose，动画问题会简单很多。

3. **材质先 bake，骨架不要 bake 掉**  
   短中期可以接受 bake 外观，但必须保留原 skinned mesh、权重、bindpose。

4. **游戏内换装系统与动画系统分层**  
   换装部件可以继续用项目自己的 `MechPartDefinition`；动作层未必需要完全依赖原 HKX。

5. **长期再决定是否复刻 AC6 shader**  
   如果需要游戏内改色/涂装/损伤，必须做 Unity 自定义 shader；如果只是开发占位，bake 更划算。

## 相关本地文档

- `E:\UnityDev\ACunicombat\太空机甲战斗开发日志\03_设计文档\AC6材质解析记录.md`
- `E:\UnityDev\ACunicombat\太空机甲战斗开发日志\03_设计文档\角色动作导入研究.md`
- `E:\UnityDev\ACunicombat\太空机甲战斗开发日志\02_已开发内容\HKX Rest Pose绑定验证.md`
