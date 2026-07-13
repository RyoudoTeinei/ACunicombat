# HKX Rest Pose绑定验证

日期：2026-07-09

## 结论

原先把原版 HKX 动画按骨骼名字直接套到 baked Melander 部件上的路线是错误的。名字匹配不等于骨架空间匹配；`.blend` 中的 Melander 部件骨架与 HKX 动画参考骨架在父子层级、本地 rest pose 和部分附加骨组上都不一致，所以会出现“蓝点骨架能动，但有模型的预览各部件漂移/散架”的现象。

当前改为新的 HKX-rest skinned 路线：

- 从 `AC6_C0000_A000_010900_pose.json` 重建一套 HKX 参考姿态骨架。
- 从 `AC6AllFramePartsPacked.blend` 复用 Melander 网格和 baked 材质来源。
- 将网格权重迁移到 HKX 骨骼名下，非 HKX 顶点组按最近骨骼/质心映射。
- 导出新的 skinned FBX：`Assets/_Project/ImportedFromBlend/MELANDER_1010_HKXSkinned/FBX/AC6_HKX_MELANDER_1010_SKINNED.fbx`。
- Unity 预览菜单已改为使用这份 FBX，并生成 `_HkxRestSkinned` clip/controller/prefab。

## 验证结果

Blender 导出侧：

- 骨骼数：130
- 网格数：16
- rest pose 旋转误差大于 1 度：0
- rest pose 位置误差大于 0.001：0
- 剩余非 HKX 顶点组的网格：0
- 仍未加权的网格：0

FBX roundtrip 侧：

- 检查骨骼：130
- 缺失骨骼：0
- 旋转误差大于 1 度：0
- 位置误差大于 0.001：0

旧 `.blend` 骨架对 HKX rest pose 的诊断显示大量父子/姿态不一致，这是旧方案散架的根因。

## Unity 内验证步骤

在 Unity 中等待脚本编译和 FBX 导入完成后：

1. 执行 `Tools > ACunicombat > Animation > Diagnose HKX Rest Skinned Model`。
2. 期望结果：checked bones 为 130，missing bones 为 0，rotation error > 1 deg 为 0，position error > 0.001 为 0。
3. 如果诊断通过，执行 `Tools > ACunicombat > Animation > Create Clean Unified Skinned HKX Preview`。
4. 只观察场景中的 `AC6_C0000_A000_010900_HkxRestSkinnedPreview_SCENE`；旧蓝点 skeleton preview、旧 unified preview 和 `Sample_PlayerMech_Rig` 会被隐藏。
5. 对话框中的 `Material overrides` 应显示有 renderer 被成功挂材质；如果为 0，需要继续修材质 key 匹配。

## 当前风险

离线 FBX 内部已经对齐，但 Unity 导入 FBX 时可能进行轴转换。若 Unity 诊断报告显示 rest pose 和 HKX 参考姿态不一致，下一步应调整 ModelImporter 的 `bakeAxisConversion` 或 Blender FBX export axis 参数，而不是继续改动画曲线。

## 2026-07-09 Unity 预览导入修正

在 Unity 中创建 `HkxRestSkinnedPreview` 后，材质覆盖已成功应用到 16/16 个 renderer，说明白模问题已不是主要阻塞。场景中模型仍横躺，结合 Blender 侧重新导入 FBX 后包围盒显示最大轴为 Y，判断优先问题是 Unity 对已经 Y-up 的 FBX 又进行了轴转换。

已将 HKX-rest FBX 的导入设置改为：

- `bakeAxisConversion = false`
- `preserveHierarchy = true`
- `optimizeBones = false`
- `optimizeGameObjects = false`

下一次执行 `Tools > ACunicombat > Animation > Create Clean Unified Skinned HKX Preview` 时会强制重新导入模型。若模型站立恢复但播放仍散架，再检查 SkinnedMeshRenderer 的 bone binding；若模型仍横躺，再改 Blender FBX export axis，而不是改动画曲线。

## 2026-07-09 预览姿态自动修正

仅切换 `bakeAxisConversion` 后，Unity 预览仍可能横躺。为避免继续手动猜 FBX/Unity 坐标轴，预览生成器现在会在模型实例外层尝试多组 90 度轴向旋转，通过网格包围盒高度和 `Head > Pelvis` 的关系选择最像站立的姿态。

这个修正只作用在 `_HkxRestSkinnedMelander` 模型实例的外层 transform，不改 HKX 骨骼曲线，不改骨骼 rest pose。生成对话框会输出 `Preview orientation` 和 `bounds`，用来确认实际应用了哪个外层旋转。

## 2026-07-09 HKX-rest 材质管线修正

站立姿态修正后，HKX 预览仍明显偏灰白。之前把问题解释为“贴图本身偏浅，所以调暗预览材质”是不严谨的；真正的问题是两条管线分叉了：

- 旧 `UnifiedSkinned` 预览更接近 `.blend` / baked 视觉来源，但骨架空间不匹配 HKX。
- 新 `HKXRestSkinned` 预览骨架空间匹配 HKX，但导出 FBX 时只复制了原始 matxml 材质名，没有在 Blender 导出阶段重新接上已验证过的 baked PNG。
- 依赖 Unity 侧按 renderer 名字后补材质太脆，不能作为主材质管线。

已撤销 HKX 预览专用的“调暗材质副本”逻辑。`AC6UnifiedSkinnedHkxPreviewImporter` 现在只直接套用 `Assets/_Project/ImportedFromBlend/MELANDER_1010/UnityMaterials` 中已有的 baked 材质，不再创建 `HkxRestPreviewMaterials`。

已将 `ExportAC6HkxRestMelanderSkinned.py` 改为读取：

`Assets/_Project/ImportedFromBlend/MELANDER_1010/AC6_BlendBaked_MELANDER_1010_manifest.json`

并在 Blender 导出 HKX-rest skinned FBX 时，按 `source_object` 给每个 mesh 直接创建 baked texture material：

- baked 材质接入：16/16 个 mesh
- 导出的 FBX：`Assets/_Project/ImportedFromBlend/MELANDER_1010_HKXSkinned/FBX/AC6_HKX_MELANDER_1010_SKINNED.fbx`
- manifest：`Assets/_Project/ImportedFromBlend/MELANDER_1010_HKXSkinned/AC6_HKX_Melander_1010_Skinned_manifest.json`

重新导出后的 FBX roundtrip 验证：

- 检查骨骼：130
- 缺失骨骼：0
- 旋转误差大于 1 度：0
- 位置误差大于 0.001：0
- mesh 数：16

下一步在 Unity 中等待 FBX 重新导入后，重新执行 `Tools > ACunicombat > Animation > Create Clean Unified Skinned HKX Preview`，重点看对话框里的 `Material overrides` 和新生成的 `AC6_C0000_A000_010900_HkxRestSkinnedPreview_SCENE`。
