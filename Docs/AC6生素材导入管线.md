# AC6 生素材 → Unity 导入管线

日期:2026-07-10
状态:管线级验证全部通过(Melander 1010 全套 + 三把武器,骨架/蒙皮/贴图/动画在 Blender 与 Unity 双端确认)。

本文档描述当前实现、工具接口、数据格式,以及在一台新机器上复现整条管线的步骤。

## 一、核心结论(为什么这条管线是对的)

以下事实均已用真实数据数值验证,不是假设:

1. **每个部件的 `skeleton.hkx` 与 c0000 基础骨架同名同拓扑,只有骨长不同。**
   把各部件 skeleton.hkx 按名覆盖到 c0000 骨架上 = 装配骨架(Melander 1010 全套合并后 219 骨,重叠覆盖 0 冲突)。**不需要"重建骨架"。**
2. **部件 FLVER 的绑定姿态与合并骨架的全局姿态严格一致**(实测最大误差 1.1mm / 0.04°,浮点噪声级)。
   因此 `bindpose = inverse(合并骨架全局矩阵)` 直接可用,顶点权重原封不动。**不需要权重迁移。**
3. **HKX 动画的 local TRS 轨道直接驱动这套骨架**,不需要 retarget。
   注意:动画中静止不动的平移轨道等于"动画录制时那套骨架的骨长",导出时**跳过静止平移轨道**,让部件自己的骨长生效(工具已实现)。
4. **Blender 完全退出几何/骨架管线**,只在需要烘焙材质时作为贴图生成器(可选)。
5. 坐标系纪律:FromSoft 与 glTF 同为右手 Y-up,**全管线零轴转换**;Unity 侧由 glTF 导入器统一做 RH→LH。任何地方都不要再加轴转换/镜像。

## 二、管线总览

```
游戏原始档 / 社区解包资源
  ├─ parts/xx_m_NNNN/  ── *.flver(模型+权重+绑定) + skeleton.hkx(部件骨架) + *.dds(贴图) + a000_8xxxxx.hkx(部件动画)
  ├─ wp/wp_NNNN/       ── 武器同上
  └─ chr/c0000*.anibnd.dcx ── Skeleton.hkx + 动画hkx + a000_XX.compendium + TAE(事件)
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
  Unity:SkinnedMeshRenderer(共享骨架) + AnimationClip + 挂点空节点
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
- **IBM**:统一 `inverse(骨骼全局)`;部件用到的骨优先取 FLVER 自己的全局(两者已验证相等)。
- **刚性网格**(`UseBoneWeights=false`):顶点存在骨骼局部空间,导出时先用骨骼全局矩阵变换到模型空间再以权重 1 绑定该骨(喷口等小件都是这类)。
- **贴图推断**:AC6 FLVER 材质不含贴图路径(在 matbin 里,解包资源缺)。按命名约定推断:
  部件 `{PART}_01_{a,n,em}`;`Marking` 材质 → `MarkingEmblem_a`;`Limiter` → 不贴图;
  武器去掉手别标记 `WP_[LRE]_NNNN → WP_NNNN`,多材质武器按材质序号 `_01/_02`。
  完整的"材质名→MTD→贴图"清单会写进每个 glTF material 的 `extras`(mtd + textures 字典),Unity 侧可读。
- **动画通道**:旋转恒定/平移恒定的骨不写对应通道(平移尤其重要——避免动画源骨长覆盖部件骨长);四元数已做半球连续化。
- **挂点**:每个 FLVER dummy 输出为节点 `{部件名}_dmy_{refID}`,挂在依附骨下(位置已换算到骨骼局部空间)。已知:机体背部/肩部 `ref=800` 系列 = 推进器喷口安装位(同一喷口网格实例化到多个挂点);武器口/特效等参照游戏惯例后续对号。
- **坐标**:全程无轴转换(见核心结论 5)。

### 4. Unity 侧

- `Packages/manifest.json` 已含 `com.unity.cloud.gltfast: 6.19.0`。`.glb` 放进 Assets 即自动导入为带 SkinnedMeshRenderer + AnimationClip 的 prefab。
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

1. **换装系统集成**(建议最优先):`MechPartDefinition` 加蒙皮部件路径;`MechAssembler` 装配时实例化共享骨架、把各部件 `SkinnedMeshRenderer.bones[]` 按骨名重绑到共享骨架实例;武器/喷口按 `*_dmy_*` 挂点挂接(喷口要实例化到全部 ref=800 位)。
2. **动画批量导出**:compendium 已解锁 c0000 全部动作,批量 hkx→pose.json→AnimationClip(repo 里 `AC6OriginalHkxAnimationImporter.cs` 的 JSON→clip 代码可直接复用,它与 pose JSON 同 schema)。root motion 在 Master/Root 轨道里;动作事件在 TAE(已随 anibnd 解出,SoulsFormats 可读)。
3. **涂装 shader**:解析 `_3m` 掩码通道 + Zone 颜色,做实 `ACunicombat/AC6/Parts Approx`,实现游戏内改色。材质参数的最终真源是游戏的 `allmaterial.matbinbnd.dcx`(解包资源里没有,需从游戏本体解)。
4. 旧管线代码(Blender 重建骨架/权重迁移、delta 曲线、22 朝向暴力尝试、`MechOriginalHkxPartRetargeter` 衰减参数)可以退役删除。

## 七、历史背景

立项原因与三条旧管线合不拢的分析见《AC6资源导入难点汇总_咨询用.md》;本地素材准备约定见《AC6本地案例资产准备.md》。仓库不提交任何游戏资源:`/Resources/`、`/Tools/LocalOut/`、`LocalSamples/AC6MelanderCase/{Raw,Generated,Reports}` 均已 gitignore,只提交工具、清单与文档。
