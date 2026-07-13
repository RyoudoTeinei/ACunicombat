# AC6 生素材 → Unity 导入管线

日期:2026-07-11（2026-07-14 更新）
状态:管线级验证全部通过(Melander 1010 + 2010 + 3010 共 12 个蒙皮部件、5 套原装/混搭 Loadout、每套两个后方推进器和四件可见武器)。

本文档描述当前实现、工具接口、数据格式,以及在一台新机器上复现整条管线的步骤。

## 一、核心结论(为什么这条管线是对的)

以下事实均已用真实数据数值验证,不是假设:

1. **每个部件的 `skeleton.hkx` 使用同一套 c0000 动画骨命名,但参考姿势会随部件改变。**
   同型号套装内部可按名合并(1010/2010/3010 各自都是 0 冲突);任意跨型号混搭不能强压成一套静态骨架。实测两套混搭出现 13–14 个共同骨冲突,最坏绑定错位约 1.16m。正确做法是每个部件保留原骨架和权重,由同一时间轴同步驱动。**不需要重建骨架或迁移权重。**
2. **部件 FLVER 的绑定姿态与合并骨架的全局姿态严格一致**(实测最大误差 1.1mm / 0.04°,浮点噪声级)。
   因此 `bindpose = inverse(合并骨架全局矩阵)` 直接可用,顶点权重原封不动。**不需要权重迁移。**
3. **HKX 动画按 reference pose 转为 delta 后驱动每个部件自己的骨架。**
   平移为 `部件静止位置 + (动画位置 - 动画参考位置)`;旋转为 `动画旋转差 × 部件静止旋转`;缩放同理。每个部件得到同长度、同动作但适配自身 bind pose 的 Clip。Unity 用一个 `MechSynchronizedAnimationPlayer` 同步所有部件 Playable。
4. **Blender 烘焙管线已彻底停用。** 几何、原权重、骨架、动作、挂点和 DDS 贴图都直接来自 FLVER/HKX/DDS;当前换装样例不得引用 `ImportedFromBlend`、`AC6RawBakedWeapons` 或由旧导入器生成的 Unity 材质。
5. 坐标系纪律:FromSoft 与 glTF 同为右手 Y-up,**全管线零轴转换**;Unity 侧由 glTF 导入器统一做 RH→LH。任何地方都不要再加轴转换/镜像。

## 二、管线总览

```
游戏原始档 / 社区解包资源
  ├─ parts/xx_m_NNNN/  ── *.flver(模型+权重+绑定) + skeleton.hkx(部件骨架) + *.dds(贴图) + a000_8xxxxx.hkx(部件动画)
  ├─ wp/wp_NNNN/       ── 武器 FLVER + DDS
  ├─ parts/wp_*.partsbnd.dcx / bs_*.partsbnd.dcx
  │    └─ binder ID 400 的 *.anibnd ── 装备 skeleton.hkx + 真 compendium + 全部动作 HKX + TAE
  └─ chr/c0000*.anibnd.dcx ── 机体 Skeleton.hkx + 动画 HKX + a000_XX.compendium + TAE
        │
        │  [AC6AnimExportProbe]  --extract-bnd 解 anibnd(需 oo2core dll)
        │  [AC6HkxPoseExporter]  skeleton.hkx → skeleton JSON;动画hkx + compendium → pose JSON(全采样 local TRS)
        ▼
  skeleton JSON(每部件一份 + c0000 基础) + pose JSON(每个动作一份)
        │
        │  [AC6PartToGltf]  合并骨架 → 验证 FLVER 对齐 → 蒙皮 GLB(嵌入贴图/动画/挂点)
        │  [texconv]        DDS → PNG(BC7 支持)
        ▼
  部件/整机 GLB(标准 glTF 2.0 binary)
        │
        │  [glTFast 6.19.0](已在 Packages/manifest.json)
        ▼
  Unity
    ├─ 框架部件:SkinnedMeshRenderer + 原骨架 + 部件专属 delta Clip
    │    └─ MechSynchronizedAnimationPlayer(机体动作公共时间轴)
    └─ 武器/推进器:各自原骨架 + 全部原生动作 Clip
         └─ MechEquipmentAnimationPlayer(每件装备独立时间轴)
```

## 三、新机器环境准备

1. **dotnet 9 SDK**、**Unity 6000.5.2f1**(工程版本)、Git。
2. **DSAnimStudio RC6.1 DLL**(SoulsFormats/SoulsAssetPipeline/Havoc):
   从 https://github.com/Meowmaritus/DSAnimStudio/releases 下载 `DSAS-V5-RC6.1.zip`,
   解压到 `<仓库上级目录>/Tools/DSAnimStudio/DSAS-V5-RC6.1/`(csproj 的 HintPath 与工具运行时都按此路径查找;也可设环境变量 `AC6_DSAS_DIR` 指向该目录)。
3. **Oodle 解压库**:把 `oo2core_6_win64.dll`(或 8/9)复制到三个工具的 `bin/Release/net9.0/` 目录。
   来源:游戏目录,或解包资源包自带的 `Extraction and conversion/UXM...7z` / `WitchyBND...7z` 里就有。
   ⚠️ **必须直接运行工具的 `.exe`**;用 `dotnet xxx.dll` 启动时原生 dll 按 dotnet.exe 目录搜索,会报 NoOodleFound。
4. **texconv.exe**(DDS→PNG):https://github.com/microsoft/DirectXTex/releases 单文件下载即可。
5. **解包素材**:部件/武器/角色目录(FLVER+DDS+HKX)。目录结构见上图。仓库不含任何游戏资源(`/Resources/`、`/Tools/LocalOut/` 已 gitignore)。
6. 构建:`dotnet build Tools/DotNet/<工具名> -c Release`(×3)。

## 四、工具接口

### 1. AC6HkxPoseExporter — HKX → JSON

```
# 骨架(部件 skeleton.hkx 或 c0000 的 4000000_Skeleton.hkx)
AC6HkxPoseExporter.exe --skeleton Skeleton.hkx --skeleton-only --out c0000_skeleton.json [--compendium xxx.compendium]

# 动画(全采样每帧每骨 local TRS)
AC6HkxPoseExporter.exe --skeleton Skeleton.hkx --anim a000_010900.hkx --compendium a000_00.compendium --out pose.json [--max-frames N] [--pretty]
```

- AC6 的动画 hkx 全部是 TCRF tagfile,**必须**传对应 anibnd 里的真 compendium。
- 骨架 hkx 一部分自含类型段可直接读;读不了的(TCRF)用下面的 `--make-compendium` 借类型段。

**JSON schema**(骨架与动画共用):
```json
{
  "Duration": 12.0, "FrameDuration": 0.0333, "ExportedFrameCount": 361,
  "Bones": [{ "Index": 0, "Name": "Master", "ParentIndex": -1,
              "ReferenceLocalTransform": { "Translation": {X,Y,Z}, "Rotation": {X,Y,Z,W}, "Scale": {X,Y,Z} } }],
  "Frames": [{ "Frame": 0, "Time": 0.0, "LocalTransforms": [ /* 与 Bones 同序同长 */ ] }]
}
```

### 2. AC6AnimExportProbe — 解包与诊断

```
--list-bnd <xxx.anibnd.dcx>                    # 列出 BND 内容
--extract-bnd <xxx.anibnd.dcx> <输出目录>       # 解出 hkx/compendium/TAE(需 oo2core)
--make-compendium <TCRF文件> <类型源hkx> <输出>  # 借 TYPE 段合成 compendium(只对骨架类可靠,动画类必须用真 compendium)
--flver-summary <xxx.flver>                    # FLVER 节点/网格/材质摘要
--compare-flver-pose <xxx.flver> <skeleton.json> [--summary]  # FLVER vs HKX 骨架对比
```

### 3. AC6PartToGltf — FLVER → 蒙皮 GLB(本管线核心,新写)

```
AC6PartToGltf.exe
  --skeleton-json c0000_skeleton.json          # 基础骨架(必填)
  --part-skeleton-json skel_lg_m_1010.json     # 部件骨架覆盖(可多个;推荐每个部件都传)
  --flver LG_M_1010.flver                      # 部件模型(可多个 → 合成一台整机)
  --anim pose.json                             # 可选:嵌入动画
  --tex-dir <PNG目录>                          # 可选:贴图查找目录(此外还会找 flver 同目录)
  --out out.glb
  --report report.json                         # 可选:对齐验证+材质清单报告
  --verify-only                                # 只验证不导出
  --dump-dummies                               # 打印 FLVER 挂点(ref ID / 位置 / 依附骨)
  --verbose
```

实现要点(改代码前先读这段):
- **骨架合并**:base 骨架按 `--part-skeleton-json` 顺序覆盖 local TRS;同名骨被多个部件覆盖时校验一致性(>1mm 记为 conflict 并报告)。FLVER 独有骨(AP_* 机构骨等)自动 graft 到合并骨架(部件骨架里一般已含,graft 是兜底)。
- **验证**:对每个网格实际使用的共享骨,比较 FLVER 全局(自身父链组合;Disabled 无父级骨存的本来就是模型空间全局)vs 合并骨架全局,输出 max/avg 位置误差与旋转角误差。**改动骨架逻辑后必须先跑 --verify-only 看这个数**。
- **两套蒙皮矩阵**:`FLVER.Mesh.Dynamic` 不只是优化标记。`Dynamic != 0` 的顶点在模型空间,使用 `当前骨骼全局 × inverse(bind 全局)`;`Dynamic == 0` 的装备顶点在各自刚体骨局部空间,使用 `当前骨骼全局 × identity IBM`。glTF 的 IBM 绑定在 skin 而不是 primitive 上,所以导出器为同一骨架建立 `AC6_DeformSkin` 和 `AC6_ReferenceSpaceSkin`,再按 mesh 分组。把两类网格强塞进一套 inverse-bind skin 会让原静止缩放为 0.001/0.01 的刀刃 SFX 在动作中放大数百倍。
- **刚性网格**(`UseBoneWeights=false`):AC6 装备常把每个顶点所属刚体骨索引放在 `Vertex.NormalW`,并非整个 mesh 共用 `Mesh.NodeIndex`。导出器优先把每个顶点以权重 1 绑定 `NormalW` 指向的骨;只有没有有效 `NormalW` 时才回退到 `Mesh.NodeIndex`。该骨索引会配合上面的对应 skin,使枪机、弹匣、发射架、能量刃和喷口机构保留原动画而不破坏比例。
- **贴图推断**:AC6 FLVER 材质不含贴图路径(在 matbin 里,解包资源缺)。按命名约定推断:
  部件 `{PART}_01_{a,n,em}`;`Marking` 材质 → `MarkingEmblem_a`;`Limiter` → 不贴图;
  武器去掉手别标记 `WP_[LRE]_NNNN → WP_NNNN`,多材质武器按材质序号 `_01/_02`。
  完整的"材质名→MTD→贴图"清单会写进每个 glTF material 的 `extras`(mtd + textures 字典),Unity 侧可读。
- **动画通道**:HKX 的绝对 local TRS 先相对动画 reference pose 求 delta,再应用到当前部件的 merged rest pose。只有 delta 始终为零的通道不写;四元数已做半球连续化。这是跨型号换装动作不断裂的关键。
- **挂点**:每个 FLVER dummy 都按完整 `Forward/Up/Position` 生成 TRS,挂在 `ParentBoneIndex` 对应骨下,并在 glTF `extras` 写入 ref/attach/parent 编号。`System.Numerics.Matrix4x4.CreateWorld` 把参数 `forward` 当作局部 `-Z`,而 FLVER `Forward` 要映射到 glTF 局部 `+Z`,所以实现必须使用 `CreateWorld(Vector3.Zero, -Forward, Up) * Translation(Position)`。不能只导位置,也不能用 `AttachBoneIndex + inverse global` 反算;这些旧算法会让武器或喷口错位。该修正会让旧版依赖错误 dummy 轴的武器整体反向,因此原生武器在 Unity 安装边界统一使用 `LocalEulerAngles=(0,180,0)`,并以枪口/工作端朝向断言防回归。当前样例只使用核心后方的两个 `ref=800` 安装推进器（前方同编号节点是效果点）,使用手臂的 `ref=23/20` 安装左右手枪械、`ref=25` 安装左臂近战武器、核心 `ref=13/14` 安装左右背武器。
- **坐标**:全程无轴转换(见核心结论 5)。

### 4. Unity 侧

- `Packages/manifest.json` 已含 `com.unity.cloud.gltfast: 6.19.0`。`.glb` 放进 Assets 即自动导入为带 SkinnedMeshRenderer + AnimationClip 的 prefab。
- `MechPartDefinition.VisualRigMode = SynchronizedSkinned` 表示部件保留自己的蒙皮骨架;`SkinnedAnimationClip` 保存该部件的 bind-relative Clip。
- `MechAssembler` 装配后把四个框架蒙皮部件交给 `MechSynchronizedAnimationPlayer`;播放器使用一个手动时间轴和多个 `AnimationPlayableOutput`。每帧评估后按依赖顺序执行接口所有权:腿先驱动核心的 4 根根链骨,已对齐的核心再驱动头的 10 根与手臂的 20 根接口祖先骨,共 34 根;部件专属骨和原权重不变。
- 武器和推进器保留各自的装备骨架与全部 HKX Clip。`MechPartDefinition.EquipmentAnimations` 保存动作 ID→Clip,装配器为每个视觉实例建立 `MechEquipmentAnimationPlayer`;机体移动、武器发射/装填、背武器展开和推进器机构动作互不覆盖。游戏逻辑调用 `PlayAction("a000_...")` 即可切换真实装备动作。推进器资源根安装到当前核心自己的后部 `ref=800`,安装平移为零、缩放为一，并在原生装备边界统一使用 `LocalEulerAngles=(180,0,0)` 修正可见模型前后轴；若保持 identity，喷嘴出口会朝向核心。`_dmy_31/41/51` 是资源内部的排焰/特效点,不能拿来反推整个模型的安装平移。装配时采样 `a000_000001` 第 0 帧作为稳定后下展示姿态,但不自动播放时间轴。排气验证直接使用 Unity dummy 的 `+forward`；旧版 `-forward` 会与反装模型互相抵消，产生数值通过但外观装反的假阳性。
- `MechMountPointDefinition.sourceTransformNameSuffix` 指定导入模型内的 dummy;`matchAllSourceTransforms` 可为同一 ref 注册多个挂点。`MechPartDefinition.instantiateVisualAtAllMounts` 让一个逻辑部件在全部匹配挂点生成视觉实例,但质量、功耗和运行时部件记录只计算一次。
- `Tools/BuildAC6NativeEquipmentSamples.ps1` 从游戏 `partsbnd` 的 binder ID 400 解出装备 anibnd,使用每件装备自己的真 compendium 批量解码全部动作,再把原 FLVER、DDS 和多段动作直接写入 GLB。`WP_L_1410` 当前原包只有骨架/TAE、没有本地 compendium/动作,管线会如实生成静态装备而不会伪造 Clip。
- `Tools/ACunicombat/AC6/Build And Validate Skinned Modular Samples` 会生成 12 个框架部件、三种原生推进器和 5 个 Loadout(1010/2010/3010 原装 + Mixed A/B)。每套样例安装左右手武器、左右背武器和两个后方推进器实例。验证会检查空骨、独立骨架数、34 个接口骨、完整 dummy 父挂点、原生资源路径、每件装备真实动作数量、首选装备动作采样、全部武器工作端朝向、推进器资源根到核心 socket、最终排气轴和静止/动作包围盒,并输出固定视角与装备近景截图。2026-07-14 回归测试为 5/5 通过,共享接口位置/旋转/缩放误差均为 0,推进器根到接口距离为 0m,排气俯角为 `-19.97°`;焰点与接口的距离只保留作诊断,不能再作为安装正确的通过条件。
- `Assets/_Project/Editor/AC6GltfSmokeTest.cs`:批处理冒烟测试。把 GLB 放到
  `Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/`,然后:
  ```
  Unity.exe -batchmode -projectPath <工程> -executeMethod AC6GltfSmokeTest.Run -quit -logFile smoke.log
  ```
  它会实例化每个 GLB、检查蒙皮/骨数、采样动画两帧、断言包围盒合法,并截图到 `LocalSamples/AC6MelanderCase/Reports/`。退出码 0 = 通过。
- ⚠️ 坑:如果手动删过 `Library/PackageCache`,必须连 `Library/PackageManager` 一起删,否则 UPM 以为包还在、不重新解包,GLB 会退化成 DefaultAsset。

## 五、端到端示例(Melander 1010 全套)

```bash
GAME=<解包素材根目录>; OUT=Tools/LocalOut
EXPORTER=Tools/DotNet/AC6HkxPoseExporter/bin/Release/net9.0/AC6HkxPoseExporter.exe
PROBE=Tools/DotNet/AC6AnimExportProbe/bin/Release/net9.0/AC6AnimExportProbe.exe
GLTF=Tools/DotNet/AC6PartToGltf/bin/Release/net9.0/AC6PartToGltf.exe

# 1. 骨架 JSON
$EXPORTER --skeleton "$GAME/chr/AC - c0000/Skeleton.hkx" --skeleton-only --out $OUT/c0000_skeleton.json
for p in hd_m_1010 am_m_1010 lg_m_1010; do
  $EXPORTER --skeleton "$GAME/parts/$p/skeleton.hkx" --skeleton-only --out $OUT/skel_$p.json; done
# bd/bs 的 skeleton.hkx 是 TCRF,先借类型段:
$PROBE --make-compendium "$GAME/parts/bd_m_1010/skeleton.hkx" "$GAME/chr/AC - c0000/Skeleton.hkx" $OUT/bd.compendium
$EXPORTER --skeleton "$GAME/parts/bd_m_1010/skeleton.hkx" --compendium $OUT/bd.compendium --skeleton-only --out $OUT/skel_bd_m_1010.json
# (bs_a_1010 同理)

# 2. 动画 JSON(需要游戏原版 anibnd)
$PROBE --extract-bnd Resources/chr/c0000_a000_00.anibnd.dcx $OUT/anibnd/c0000_a000_00
$EXPORTER --skeleton $OUT/anibnd/c0000/4000000_Skeleton.hkx \
  --anim $OUT/anibnd/c0000_a000_00/1000010900_a000_010900.hkx \
  --compendium $OUT/anibnd/c0000_a000_00/7000000_a000_00.compendium --out $OUT/pose_010900.json

# 3. 贴图 PNG
texconv.exe -ft png -w 1024 -h 1024 -m 1 -y -o $OUT/tex "$GAME/parts/bd_m_1010/BD_M_1010_01_a.dds"  # 各部件 a/n/em 同理

# 4. 整机 GLB(含验证,先看 alignErr 是否 ≈0)
$GLTF --skeleton-json $OUT/c0000_skeleton.json \
  --part-skeleton-json $OUT/skel_lg_m_1010.json --part-skeleton-json $OUT/skel_bd_m_1010.json \
  --part-skeleton-json $OUT/skel_am_m_1010.json --part-skeleton-json $OUT/skel_hd_m_1010.json \
  --flver "$GAME/parts/lg_m_1010/LG_M_1010.flver" --flver "$GAME/parts/bd_m_1010/BD_M_1010.flver" \
  --flver "$GAME/parts/am_m_1010/AM_M_1010.flver" --flver "$GAME/parts/hd_m_1010/HD_M_1010.flver" \
  --anim $OUT/pose_010900.json --tex-dir $OUT/tex --out $OUT/Melander_1010.glb --report $OUT/verify.json

# 5. 拷进 Unity 并跑冒烟测试(见第四节)
```

武器:`--skeleton-json` 直接传武器自己的 `skel_wp_XXXX.json`,`--flver WP_L_XXXX.flver`,单独出 GLB。

## 六、未完成事项(接手开发的起点)

1. **动画批量导出**:compendium 已解锁 c0000 全部动作,批量 hkx→pose.json→每部件 delta AnimationClip。root motion 在 Master/Root 轨道里;动作事件在 TAE(已随 anibnd 解出,SoulsFormats 可读)。
2. **涂装 shader**:解析 `_3m` 掩码通道 + Zone 颜色,做实 `ACunicombat/AC6/Parts Approx`,实现游戏内改色。材质参数的最终真源是游戏的 `allmaterial.matbinbnd.dcx`(解包资源里没有,需从游戏本体解)。
3. **骨架裁剪/性能**:当前每个部件 GLB 保留完整 c0000 动画骨以保证路径兼容;量产前可裁掉不参与该部件蒙皮且不作为祖先/挂点的分支。

## 六点五、贴图外观说明与 Blender 遗产清除(2026-07-12)

**"肢体银色、武器有贴图"不是管线差异。** 逐 GLB 核实(`asset.generator` 均为 `AC6PartToGltf`,肢体嵌 albedo/normal/emissive/Marking 四图并已绑定):武器与肢体走完全相同的原生管线。像素统计显示两者的 `_a` albedo 都是**中性灰**(彩度 0.1–0.9/255)——AC6 里肢体和武器都可涂装,最终颜色统一来自 Zone 涂装系统(`_3m` 掩码 × 调色板,即第六节第 2 项待办)。武器"看起来有贴图"只是机械细节对比度高;装甲大平面 + 中性灰 + 金属度参数渲出来就是银色。这是预期状态,不是缺陷。

**Blender 管线已从仓库物理删除**(约 255 文件):`Tools/Blender/` 全部脚本、`AC6BlendBakedPartsImporter`、`AC6RawBakedWeaponImporter`、`AC6UnifiedSkinnedHkxPreviewImporter`、`AC6RiggedMelanderPreviewBinder`、`AC6RiggedAnimationProbeImporter`、`MechOriginalHkxPartRetargeter`(运行时+编辑器)、`ImportedFromBlend/`、`ImportedFromExtracted/`、`Data/Mechs/{AC6BlendBakedSamples,AC6RawBakedWeapons}`。保留:`AC6OriginalHkxAnimationImporter`(原生 HKX 蓝点验证路径,JSON→Clip 参考实现,已断开对 Blender 预览链的调用)、`ImportedFromGame/`(原生动画产物)、`_Imported/`(路线 A 原始 FBX 参考,非 Blender)。已知无害残留:`SampleScene.unity` 里有一个已删除组件的 missing-script 引用,可用 `Tools > ACunicombat > Clean Legacy Scene Objects` 或手动移除。

## 七、历史背景

立项原因与三条旧管线合不拢的分析见《AC6资源导入难点汇总_咨询用.md》;本地素材准备约定见《AC6本地案例资产准备.md》。仓库不提交任何游戏资源:`/Resources/`、`/Tools/LocalOut/`、`LocalSamples/AC6MelanderCase/{Raw,Generated,Reports}` 和 `LocalSamples/AC6SkinnedModular/{Raw,Generated,Reports}` 均已 gitignore,只提交工具、清单与文档。
