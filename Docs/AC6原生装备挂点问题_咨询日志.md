# AC6 原生装备挂点问题咨询日志

更新时间：2026-07-11

## 1. 当前结论

本工程已经跑通一条**不经过 Blender**的 AC6 生素材管线：

```text
游戏 partsbnd / anibnd / TPF
  -> FLVER + skeleton.hkx + animation.hkx + DDS
  -> AC6HkxPoseExporter / AC6AnimExportProbe
  -> AC6PartToGltf
  -> 带蒙皮、贴图、动画与 dummy 节点的 GLB
  -> Unity glTFast
```

目前机体四类框架部件（头、核心、手臂、腿）的自由换装与同步播放已经基本成立，1010、2010、3010 及两套混装不会再因共享骨骼覆盖而散架。

**尚未解决的是武器和推进器的挂点矩阵。** 武器模型、贴图、独立骨架和原始 HKX 动作已经能直接导入，但装备根节点与机体 FLVER dummy 之间的坐标/朝向换算仍未完成视觉验证。当前仓库包含一个刚调整、尚未完成全量回归的候选实现，不能视为最终答案。

## 2. 需要特别澄清的事项

现在使用的武器并非 Blender 烘焙资源。样例路径为：

```text
Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment/*_NATIVE.glb
```

这些本地 GLB 由 `Tools/BuildAC6NativeEquipmentSamples.ps1` 调用 `AC6PartToGltf` 直接生成。GLB 的 `asset.generator` 是 `AC6PartToGltf`。武器有颜色，是因为管线从 TPF 提取 DDS、转换纹理并写入 GLB；“有贴图”不等于经过 Blender。

`Raw`、`Generated`、`Reports`、`Resources` 和 `Tools/LocalOut` 均被 `.gitignore` 排除，不会把游戏原始资源提交到仓库。

## 3. 已确认可用的部分

### 3.1 框架换装与机体动画

- 每个头、核心、手臂、腿保留自己的 `SkinnedMeshRenderer`、骨架和 bind pose。
- 每个部件 GLB 都包含同一个机体 HKX 动作，但动画按“源参考姿势到当前帧的 delta”重定向到该部件自己的 rest pose。
- `MechSynchronizedAnimationPlayer` 用一个共享时间轴驱动四个独立 Animator。
- 腿驱动核心接口祖先骨，核心再驱动头和手臂的接口祖先骨；共同步 34 根接口骨，部件专属骨不被覆盖。
- 1010、2010、3010 及 Mixed A/B 的接口位置、旋转、缩放误差可以达到 0。

### 3.2 FLVER 两类 mesh 空间

AC6 的 `mesh.Dynamic` 不只是优化标记：

- `Dynamic != 0`：顶点按模型空间/常规 inverse bind 处理。
- `Dynamic == 0`：部分顶点按骨骼参考空间存储，需要 identity inverse-bind 的第二套 skin。
- 非权重 mesh 还可能用顶点 `NormalW` 指定刚性骨。

`AC6PartToGltf` 当前使用双 skin，并处理 `NormalW`。这是武器机构能保持完整并播放原动作的必要条件。

### 3.3 装备独立动作

- 武器和推进器不共用机体 Animator。
- `MechEquipmentAnimationPlayer` 为每个装备视觉实例保留自己的动作表与 PlayableGraph。
- 当前样例动作数量：
  - `WP_L_0010`：6
  - `WP_L_0230`：6
  - `WP_L_0250`：5
  - `WP_L_4210` / `WP_R_4210`：各 14
  - `WP_R_4410`：17
  - 每个推进器样例：4

## 4. 已纠正的武器分类错误

早期样例错误地把以下文件当成背部武器：

- `WP_R_1310`
- `WP_L_1410`
- `WP_R_1410`

读取游戏 `regulation.bin` 后确认它们是手持榴弹/火箭筒，`WepAbsorpPosParam` 为 100，不是背部槽位。

当前背部样例改为真正的背武器：

- `WP_L_4210`
- `WP_R_4210`
- `WP_R_4410`

参数映射已确认：

```text
WepAbsorpPos 100（手持枪械）
  Right = 3020
  Left  = 3023

WepAbsorpPos 200（近战）
  Right = 3020
  Left  = 3025

WepAbsorpPos 300（背部武器）
  Right = 2014
  Left  = 2013
```

DSAnimStudio 对 AC6 前缀的解释为：

```text
1xxx -> HeadPart
2xxx -> BodyPart（本工程 Core）
3xxx -> ArmsPart
4xxx -> LegsPart
17xxx -> AC6BackRightWeaponRail
19xxx -> AC6BackLeftWeaponRail
```

因此 `2013/2014` 必须在 Core 上寻找 dummy 13/14，不能在 Arms 上寻找。该槽位来源错误已经在 `AC6SkinnedModularSampleBuilder` 中改正。

## 5. 当前核心难点：FLVER dummy 到 glTF socket 的矩阵

FLVER dummy 同时提供：

- `Position`
- `Forward`
- `Upward`
- `UseUpwardVector`
- `ParentBoneIndex`
- `AttachBoneIndex`
- `Flag1`（follow flag）

而 glTF 节点只能有一个父节点和一个局部 TRS。需要把 FromSoftware/DSAnimStudio 的 dummy 计算完整压缩成一个 glTF local transform。

DSAnimStudio 的相关逻辑位于：

```text
DSAnimStudioNETCore/NewDummyPolyInfo.cs
DSAnimStudioNETCore/NewDummyPolyManager.cs
DSAnimStudioNETCore/NewChrAsm.cs
```

关键表达式大致是：

```csharp
ReferenceMatrix = CreateWorld(forward, up)
                * Translation(position)
                * ParentBone.ReferenceFKMatrix;

AttachMatrix = Flag1 ? AttachBone.RelativeMatrix : Identity;
CurrentMatrix = ReferenceMatrix * AttachMatrix;
```

此外 AC6 武器吸附还叠加 `RotationX(PI)` 及武器 backwards/sideways/upside-down 翻转。DSAnimStudio 使用 XNA/System.Numerics 的行向量式矩阵顺序，而 glTF/Unity 采用父子局部 TRS，矩阵乘法、四元数和坐标约定必须一起核对。

## 6. 已尝试的三版 dummy 导出

### 6.1 旧版：仅位置，挂在 AttachBone

```csharp
local = Translation(dummy.Position) * inverse(attachBoneBindGlobal);
parent = AttachBoneIndex;
```

结果：

- 武器位置大致靠近机体。
- dummy 朝向被完全丢失，武器和喷口方向错误。
- 这是用户最初看到“武器挂上了但位置/朝向乱”的版本。

### 6.2 失败版：完整姿态直接挂在 ParentBone

```csharp
local = CreateWorld(forward, up) * Translation(position);
parent = ParentBoneIndex;
```

结果：

- 结构验证一度显示 5/5 通过。
- 自动截图清楚显示手持武器和背武器漂浮在机体下方数米处。
- 原因是把 FLVER 中看起来处于模型空间的 dummy 位置再次当成骨骼局部位置，产生重复偏移。
- 这也暴露了现有验证阈值过松：只检查总包围盒小于 30 米、装备距总包围盒中心小于 15 米，错误场景仍能通过。

因此 **2026-07-11 18:42 生成的“5/5 PASS”报告是伪通过，不能作为正确性证据。**

### 6.3 当前候选版：完整模型空间姿态重表达到 AttachBone

仓库当前 `AC6PartToGltf/Program.cs` 使用：

```csharp
socketModel = CreateWorld(forward, up) * Translation(position);
local = socketModel * inverse(attachBoneBindGlobal);
parent = AttachBoneIndex;
```

它结合了旧版较正确的位置与新版完整朝向。对 `BD_M_1010` 单文件探针可看到 dummy 13/14 已恢复到旧版附近的局部位置，同时拥有非 identity 四元数。

### 6.4 结论（2026-07-11,已对照 DSAnimStudio 真实源码逐行核实)

阅读 `NewDummyPolyInfo.cs` / `NewDummyPolyManager.cs` / `NewChrAsm.cs`(GitHub master)后确认:

1. **dummy 的 Position/Forward/Upward 在 ParentBoneIndex 的参考空间**(构造时乘 `ParentBone.ReferenceFKMatrix`);AC6 部件的 ParentBone 全部是 identity 的无父级容器节点(`*_1400_*`、`*_Dmypoly`,已实测),所以数值上等于模型空间。Forward/Upward **不是单位向量**(实测模长 0.18–6),必须归一化。
2. **Flag1 决定是否跟随 AttachBone 的动画**:`CurrentMatrix = ReferenceMatrix × AttachBone.RelativeMatrix(Flag1 时)`。6.3 的单父级编码与该规则逐项等价,是**正确答案**,不再是假设。已补两处:local 组合 `parentRefGlobal`(容器为 identity 时无影响,保通用性);`Flag1=false` 时改挂静态参考位、不挂 AttachBone。
3. **武器安装规则:AC6 所有武器槽位统一为 `weaponWorld = RotationX(π) × dummyCurrent`**(NewChrAsm.DoWPN),没有左右手/背部/rail 差异,`FlipBackwards/Sideways` 对 AC6 均为 false,不存在 RotationZ(π)。Unity 侧编码:挂点 mount 的 `localEulerAngles = (180, 0, 0)`,武器实例相对 mount 为 identity。已写入 `BuildFrameMountPoints`。推进器在 DSAS 中未实现(`AC6Booster_NotImlementedYet`),喷口先用 identity,以截图定夺。
4. **武器自身没有吸附锚点**,FLVER 模型空间原点即安装原点;武器自带 dummy 是枪口(ref=10,朝 -Z)/SFX 点。
5. rail(17xxx/19xxx)与背挂点(2013/2014)的切换是武器收放状态切换,由游戏逻辑/TAE 驱动;本工程用自己的挂点状态机实现,不复刻 TAE。
6. 验证金标(1010,FromSoft 模型空间):右手 dmy_20 `(-2.271, 7.906, -1.971)`、左手 dmy_23 `(2.270, 7.904, -1.974)`、背左 dmy_13 `(1.458, 10.013, 1.856)`、背右 dmy_14 `(-1.458, 10.013, 1.856)`、喷口 dmy_800 ×3 `(0, 8.622, -2.225) / (±0.413, 8.944, 2.402)`。已作为硬断言写进 `AC6SkinnedModularSampleBuilder`。金标必须在**未动画的原始 GLB 实例**上测(装配好的机体在验证时已处于动画姿态);Unity 侧坐标 = FromSoft 坐标取 X 反号(glTFast RH→LH)。

### 6.5 回归结果(2026-07-11,本机重新生成全部 22 个 GLB 后)

- 5/5 loadout 全部 PASS(退出码 0),七个金标 socket 误差 0.2–0.9mm。
- 装备到框架包围盒的最近距离(新验证器按框架 only 包围盒计算)全部 0m;安装变换在静止与动作中点零漂移。
- 截图人工核查:步枪正确握持于左手、枪管朝前;背部武器挂载正确;混装(MixedB 反关节)同样正确。
- 验证器改进已落地:动作数改读 `AC6NativeEquipmentManifest.json`(无游戏 partsbnd 的机器如实生成 0 动作装备并按 0 验证);金标断言;框架 only 距离;安装恒等断言。
- 本机(无游戏本体)装备 GLB 不含原生动作;有 partsbnd 的机器跑 `Tools/BuildAC6NativeEquipmentSamples.ps1` 重新生成即可恢复动作,验证器自动按 manifest 数量断言。

## 7. 推进器的额外问题

- Core 上通常有三个 ref 800 dummy，样例会生成三个推进器视觉实例。
- 逻辑部件只计一次质量/功耗，视觉与装备动画播放器按三个挂点实例化。
- 当前需验证每个 ref 800 的位置和喷口朝向。
- 推进器自身 GLB 也有独立骨架与 4 个 HKX 动作，不能退回静态 prefab 或 Blender 烘焙件。

## 8. 验证器需要补强

当前 `AC6SkinnedModularSampleBuilder` 已能检查：

- 四个框架蒙皮实例与独立骨架是否存在。
- 34 根接口祖先骨是否同步。
- 真正的装备资源路径是否位于 `Raw/NativeEquipment`。
- 装备动作数与默认动作能否采样。
- 武器是否挂到预期 dummy 后代。

仍需增加：

1. 只计算框架部件包围盒，要求每个装备挂点到框架包围盒的距离接近 0，而不是和“包含错误装备后的总包围盒中心”比较。
2. 对手持武器根、背武器根、三个推进器根分别记录世界坐标和到对应 dummy 的相对 TRS。
3. 截图相机使用独立 Layer/culling mask。当前预览场景截图会拍到主场景中旧的验证对象，画面容易混淆。
4. 静止与动作中点分别检查；装备自身 HKX 动作不能改变装备根节点相对挂点的安装变换。
5. 最终人工检查武器枪口、背部展开方向和推进器喷口方向。

## 9. 希望咨询者重点回答的问题

1. AC6 FLVER dummy 的 `Position/Forward/Upward` 究竟是模型空间、ParentBone 空间，还是需按 `Flag1` 分情况解释？
2. `ParentBoneIndex` 与 `AttachBoneIndex` 在 AC6 中分别承担“参考姿势基准”和“动画跟随”的哪一部分？如何等价编码为单父节点 glTF 层级？
3. `CreateWorld * Translation * ParentFK * AttachRelative` 从 XNA 行向量约定转换到 glTF/Unity 时，正确的矩阵顺序和转置规则是什么？
4. DSAnimStudio 的 AC6 `RotationX(PI)`、左右侧翻转和 `RotationZ(PI)` 是否最终相互抵消，还是背武器、手持武器和 rail slot 需要不同修正？
5. 武器模型自身是否还存在一个应与目标 dummy 对齐的源 dummy/吸附锚点，而不是简单让武器 GLB 根节点落在目标 dummy？
6. `17xxx/19xxx` rail slot 与 `2013/2014` 正常背挂点的切换，是否由 TAE 事件在收纳/展开期间驱动？

## 10. 关键代码

```text
Tools/DotNet/AC6PartToGltf/Program.cs
  FLVER、双 skin、dummy、贴图和多动画写入 GLB

Tools/DotNet/AC6HkxPoseExporter/Program.cs
  HKX 骨架/动画采样与 JSON 导出

Tools/DotNet/AC6AnimExportProbe/Program.cs
  binder、compendium、TPF 诊断与提取

Tools/BuildAC6NativeEquipmentSamples.ps1
  原生武器/推进器批量管线

Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs
  12 个框架部件、5 套 loadout、装备挂点和验证场景

Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs
  运行时装配、dummy 来源挂点、多推进器视觉实例

Assets/_Project/Scripts/Mech/Runtime/MechSynchronizedAnimationPlayer.cs
  四个换装框架的同步动画与接口骨传递

Assets/_Project/Scripts/Mech/Runtime/MechEquipmentAnimationPlayer.cs
  每件原生装备的独立 HKX 动作播放
```

## 11. 本地复现步骤

游戏资源不在 Git 中。准备好本地 AC6 解包文件后：

```powershell
# 1. 生成原生武器和推进器 GLB
powershell -ExecutionPolicy Bypass -File Tools/BuildAC6NativeEquipmentSamples.ps1

# 2. 在 Unity 中执行
Tools > ACunicombat > AC6 > Build And Validate Skinned Modular Samples

# 3. 查看
Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity
Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/skinned_modular_validation.txt
Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/Validation_*_rest.png
Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/Validation_*_action.png
```

不要把一次 `PASS` 当作最终证据；必须同时检查隔离后的自动截图和装备到框架包围盒的距离。
