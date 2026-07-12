# AC6 资源导入难点汇总咨询日志

更新日期：2026-07-13

咨询问题文档：`Docs/AC6资源导入难点汇总_咨询用.md`。本日志整理外部咨询者读完 Unity 端 13 个导入器、Blender 端 15 个脚本以及运行时装配/重定向代码后给出的结论，为事后补记存档；结论中的架构方向（废弃 .blend 重建路线、共享骨架 + 按名重绑、Blender 只负责 bake 贴图）已被后续会话采纳执行。

## 1. 总体判断

三条管线里，HKX → JSON → AnimationClip 这条已经做对了，真正有毒的源头是 `AC6AllFramePartsPacked.blend` 部件包。蓝点骨架预览能正确播放动画，证明 `AC6OriginalHkxAnimationImporter` 从 `_pose.json` 直接重建骨架并写绝对 local 曲线这条路自洽且正确。缺的只有一样东西：一个"顶点 + 权重 + bindpose 都表达在 HKX 参考骨架空间里"的蒙皮网格——而它从那个社区整理的 .blend 包里榨不出来，因为该包的骨架是二手重建的，不是游戏原始 rig。

另一个被文档当成"架构冲突"的问题其实是伪问题：AC6 游戏本身就是"模块化部件 + 一套统一骨架"。每个部件（`parts/*.partsbnd.dcx` 里的 FLVER）自带一个骨骼子集，骨骼名与 c0000 的 `skeleton.hkx` 对应，装配时蒙到同一套骨架上。换装系统和原版 skinned rig 不冲突——原版就是这么做的。要做的不是二选一，而是把 `MechPartDefinition` 装配从"刚体挂点"升级为"共享骨架 + 按名重绑 `SkinnedMeshRenderer.bones`"。

## 2. 代码里的具体病灶

咨询者逐一读码确认了失败的精确位置：

1. `ExportAC6HkxRestMelanderSkinned.py` 的重绑在数学上不成立。它在 HKX rest pose 位置新建骨架，然后把 .blend 网格原封不动 parent 过去、只改 Armature modifier 指向，顶点数据本身没动。这只有在 .blend rest pose 与 HKX rest pose 恰好一致时才成立。同文件里暴力尝试 22 种 90° 朝向、按"包围盒最高 + 头在骨盆上方"打分的 `ApplyPreviewOrientation`，就是坐标约定从未被推导、一直靠猜的直接证据。
2. `MechOriginalHkxPartRetargeter` 的 "Stabilize" 默认参数（位置权重 0、旋转权重压到 0.18–0.6）是同一件事：用衰减掩盖错位，而不是修复错位。
3. 整条链路没有任何一处显式做 FromSoft（右手系）→ Unity（左手系）的转换——Blender 脚本原样消费 JSON 四元数，C# 原样拷贝 XYZW。目前它在蓝点预览里"碰巧"自洽，是因为那条路的骨架和动画来自同一个 JSON；一旦引入第二个几何来源（Blender FBX），约定不一致就会爆炸。

## 3. 对咨询问题的直接回答

**问题 1（是否存在可用的蒙皮网格源）**：有，但不是一个完整机体，而是部件本身。每个 `parts/xx_m_1010.partsbnd.dcx` 里的 FLVER 就是蒙皮网格，自带骨骼层级、bind pose、顶点权重，骨骼名对应 c0000 动画骨架。应该从 FLVER 开始处理，而不是任何已导出的 FBX（那些 FBX 出处和转换设置不可考，`c4201.fbx` 那个 838 骨的 "dummy_Anim" 是内存抓取的合并 rig，不是干净数据）。工具链：WitchyBND 解包 partsbnd/tpf，SoulsFormats 读 FLVER——`AC6AnimExportProbe` 既然能解 anibnd，这套 C# 生态已经在手上。

**问题 2（DSAnimStudio 能否导出 Unity 可用动画）**：放弃这个方向，现有方案更好。DSAnimStudio 是预览器不是管线工具。`AC6HkxPoseExporter` 输出的全采样 local TRS JSON 就是正确的中间格式，JSON → AnimationClip 的 C# 代码已验证可用（130 骨 roundtrip 零误差）。root motion（从 Master/Root 骨轨道拆出写入 root motion 曲线）和 TAE 事件（SoulsFormats 可读）后期再补。

**问题 3（Blender bake 如何不破坏 bindpose）**：正确答案是让 Blender 彻底退出几何/骨架管线。bake 的本质是往 UV 空间写 PNG，根本不需要碰骨架。贴图从 Blender 出，几何 + 权重 + bindpose 从 FLVER 转换器出，两者共享同一套 UV（都源自同一个 FLVER），在 Unity 里合流。"视觉管线"和"骨架管线"物理上分离，谁也污染不了谁。

**问题 4（程序化动作与太空机动）**：用程序动画层（Unity Animation Rigging / Playables）叠加在骨架上。`MechProceduralPoseAnimator` 思路保留，但驱动对象从"部件根节点"改为"共享骨架的骨骼"。

**问题 5（模块化换装与完整 skinned rig 的架构冲突）**：伪问题，见总体判断——游戏原版就是模块化部件蒙在统一骨架上。

## 4. 推荐管线

```text
游戏档案 ──WitchyBND/UXM──> FLVER + TPF(DDS) + matbin + skeleton.hkx + *.hkx
                                    │
                        [一个 C# CLI 工具，扩展现有 AC6HkxPoseExporter]
                                    │
        ┌───────────────────────────┼───────────────────────────┐
        ▼                           ▼                           ▼
  每部件 glTF                skeleton.json（已有）        pose.json（已有）
  (mesh+权重+                （HKX 参考骨架，唯一          （全采样动画）
   inverseBindMatrices        真源 canonical rig）
   全部表达在 HKX
   骨架空间)
        │                           │                           │
        ▼                           ▼                           ▼
   Unity glTFast导入        Unity 构建统一骨架实例      现有 JSON→AnimationClip
        │                           │                           │
        └────── 装配时按骨骼名重绑 bones[] ──────┴──── Animator 播放 ────┘
                                    ▲
  Blender 只做一件事：FLVER 材质 → bake PNG（纯 UV 空间，不导出任何几何）
  matbin 解析 → 长期换成 Unity 实时 shader
```

## 5. 关键正确性公式

这是 `ExportAC6HkxRestMelanderSkinned.py` 缺掉的那一步：若验证发现 FLVER 骨骼 rest 与 HKX 参考 rest 不一致，不要迁移权重，而是对顶点做一次线性混合重定位：

```text
v' = Σᵢ wᵢ · HkxGlobalᵢ · FlverGlobalᵢ⁻¹ · v      （法线同理，用逆转置）
bindposeᵢ = HkxGlobalᵢ⁻¹
```

权重 `wᵢ` 原封不动。这才是"换 bind pose"的正确操作。对 AC6 部件而言，FLVER rest 很可能就等于 HKX rest（游戏运行时就是直接蒙上去的），这步大概率是恒等变换——但要先验证，不要假设。

## 6. 落地步骤

1. **验证假设（半天）**：用 SoulsFormats 读 BD_M_1010 的 FLVER，把它的骨骼层级 + local transform 与 `4000000_Skeleton.hkx` 的 JSON 对比——现有 diagnostic 脚本逻辑可直接复用。结果决定重定位公式是不是恒等。
2. **打通一个部件（1–2 天）**：C# 工具输出 BD_M_1010 的 glTF（HKX 空间 bindpose），Unity 里用现有 clip 播 `a000_010900`，看它不散架。贴图先糊一张 bake 的 PNG。
3. **四部件合体**：HD/BD/AM/LG 各自导入，装配时按名重绑到同一骨架实例，验证换装 + 动画共存。`MechPartDefinition` 加"蒙皮部件"路径（存 mesh + 骨骼名列表），`MechAssembler` 学会重绑。
4. **材质分轨**：Blender bake 流程保留但改造成"只出 PNG"；并行解析 matbin，把 Parts Approx shader 做实。
5. **清理**：`AC6AnimExportProbe`、`AC6HkxPoseExporter` 等管线核心工具收编进仓库（当前游离在 `E:\UnityDev\tmp`），硬编码的 `E:\` 路径抽成配置文件。

`ExportAC6HkxRestMelanderSkinned.py`、`ExportAC6UnifiedMelanderSkinnedPreview.py`、delta 曲线方案、`MechOriginalHkxPartRetargeter` 的衰减参数——这些在新管线里全部退役，它们是在为"源头数据不对"付利息。
