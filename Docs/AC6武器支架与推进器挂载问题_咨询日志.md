# AC6 武器支架与推进器挂载问题咨询日志

更新时间：2026-07-11

## 1. 咨询目的

当前工程已经能从 AC6 生素材直接生成带蒙皮、贴图、dummy 节点和原始 HKX 动作的 glTF/GLB，并在 Unity 中完成头、核心、手臂、腿的自由换装与同步动画。现有自动验证显示 5 套 loadout 全部通过，但最新人工检查确认，装备的实际安装语义仍有明显错误。

本日志只讨论以下三个问题：

1. 手持武器方向错误，并与手掌、前臂重叠。
2. 背部武器方向和连接关系可疑，缺少游戏中可见的支架或导轨。
3. 推进器视觉似乎采用通用固定安装方式，没有正确表达不同核心部件各自的喷口位置、方向和拓扑。

本日志用于向熟悉 AC6、FromSoftware FLVER/HKX、DSAnimStudio 或 SoulsFormats 的开发者咨询。这里不再把“挂到正确 dummy”或“5/5 PASS”视为视觉正确的证据。

## 2. 当前结论

### 2.1 自动验证出现了语义层面的伪通过

目前的验证结果能证明：

- 装备实例存在且有 Renderer。
- 装备来自原生生素材管线，不是 Blender 烘焙资源。
- 装备根节点挂在预期编号的 dummy 节点下。
- 装备会跟随机体动作，安装根变换不会在动画中漂移。
- 装备包围盒与框架包围盒相交。
- 原始装备 HKX 动作已导入，并由独立播放器管理。

但是，这些条件无法证明：

- 武器握把是否落在手掌中。
- 武器枪口、刀刃或发射方向是否正确。
- 左右槽位是否在 RH 到 LH 坐标转换后保持正确。
- 武器是否穿入手掌、前臂或躯干。
- 背部武器是否通过正确支架或导轨连接。
- 背部武器当前处于收纳、部署还是错误 bind pose。
- 推进器模型是否应该整体实例化一次，还是在每个喷口 dummy 下各实例化一次。
- 每个核心的喷口数量、位置和朝向是否与原游戏一致。

因此，现有 `5/5 PASS` 只能解释为“装配结构和动画跟随通过”，不能解释为“装备安装正确”。

### 2.2 撤回上一版日志中的视觉正确结论

旧日志 `Docs/AC6原生装备挂点问题_咨询日志.md` 第 6.5 节曾记录“步枪正确握持、背部武器挂载正确”。最新近距离截图已经推翻该人工判断。该结论应视为撤回，直到本日志的验收条件全部满足。

## 3. 可复现场景

Unity 场景：

```text
Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity
```

生成与验证入口：

```text
Tools > ACunicombat > AC6 > Build And Validate Skinned Modular Samples
```

当前样例包含：

- Melander 1010
- 2010
- 3010
- Mixed A
- Mixed B

建议在 Scene 视图中分别从手掌侧面、背部侧面和核心后方近距离检查静止姿势与动作中点。当前总览相机距离太远，容易把严重的安装错误误判为正确。

## 4. 已观察到的问题

### 4.1 手持武器反向并与手部重叠

用户在最新截图中观察到：

- 被识别为右手侧的长武器或近战武器方向反了。
- 武器主体与手掌、前臂占据同一空间，未形成正常握持关系。
- 即使装备根节点严格跟随目标 dummy，这个错误也会稳定保持，因此“动画中零漂移”不能排除错误。

这里还有一个必须先澄清的矛盾：当前样例代码只配置了 `LeftHandWeapon`，没有配置 `RightHandWeapon`。三个武器槽为：

```text
LeftHandWeapon
LeftBackWeapon
RightBackWeapon
```

对应实现位于：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:981
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:982
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:983
```

所以需要先在运行时记录中确认截图中的武器究竟属于哪个 slot、哪个 `WP_*` 资源。存在以下可能：

1. 用户按画面理解的左右与代码槽位相反。
2. RH 到 LH 的 X 轴镜像使左右 dummy 或视觉方向被错误解释。
3. 当前武器并非近战武器，而是因严重穿插而被误判。
4. 场景中仍有相邻验证机体或旧对象干扰观察。

无论是哪一种，当前样例缺少“slot、资源 ID、源 dummy、世界变换”的可视化标签，诊断信息不足。

### 4.2 背部火炮方向可疑，并缺少支架或导轨

截图中的肩部火炮存在两个独立问题：

- 火炮朝向可能与原游戏相反，目前没有可靠的枪口方向基准可以自动判断。
- 火炮主体与核心之间有明显空隙，没有看到原游戏中用于连接背武器的支架、导轨或展开机构。

当前实现将背部武器根节点直接安装到核心的 dummy：

```text
LeftBackWeapon  -> _dmy_13
RightBackWeapon -> _dmy_14
```

然后统一附加 `(180, 0, 0)` 的局部欧拉角。相关代码：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:1022
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:1023
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:1024
```

当前生素材 binder 中每个测试武器只找到一个目标 FLVER，因此“漏导第二个明显的支架 FLVER”暂时没有证据。缺失支架仍可能来自：

1. 同一武器 FLVER 内的子 mesh，但当前 bind pose、骨骼姿势或动作状态让它隐藏、折叠或错位。
2. 武器骨架中的 rail、pylon 或 deploy 骨，需要先播放正确的默认动作。
3. 核心部件自身的支架 mesh 或 dummy 驱动机构。
4. 单独的装备、SFX 或行为资源，目前没有被导入。
5. `17xxx/19xxx` rail slot 与 `2013/2014` 背挂点之间的状态切换，目前被简化为直接挂载。
6. DSAnimStudio 中武器安装矩阵虽然有统一 `RotationX(PI)`，但在转换为 Unity 子节点局部 TRS 时，矩阵顺序或坐标系转换仍有错误。

这些都是待验证假设，不能在没有原游戏或 DSAnimStudio 对照数据时任选一个作为修复。

### 4.3 推进器按三个通用实例复制，尚未证明符合原游戏语义

需要澄清：当前代码并不是把所有推进器放在一个全局固定坐标。它会在所选核心中查找所有名称以 `_dmy_800` 结尾的节点，并为每个节点建立挂点。

但是，当前运行时会把同一个完整推进器 prefab 在三个挂点下各复制一次：

```text
MechPartDefinition.InstantiateVisualAtAllMounts = true
```

相关实现：

```text
Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs:98
Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs:111
Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs:115
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:1022
```

三个挂点当前都使用 identity 安装修正：

```text
localPosition    = (0, 0, 0)
localEulerAngles = (0, 0, 0)
localScale       = (1, 1, 1)
```

这只是因为 DSAnimStudio 中推进器装配参考实现仍标注为未完成，不能视为 AC6 的真实规则。

用户看到的现象是，喷口像被按通用位置硬装，而不是适配每个核心自己的喷口布局。潜在问题包括：

1. `_dmy_800` 可能代表喷焰或 SFX 发射点，不一定代表完整推进器模型根。
2. 一个推进器资源可能应该整体实例化一次，再由其内部多个骨或 dummy 对齐核心接口，而不是完整复制三次。
3. 三个 `_dmy_800` 可能对应不同类型或不同朝向的喷口，不能共用 identity 旋转。
4. 不同核心的喷口数量、布局或接口含义可能不同，不能只按 suffix 全匹配。
5. 核心外壳可能已经包含喷口几何，独立 Booster 部件只提供内部机构、喷焰或性能数据。
6. 推进器的默认 HKX 动作或 rest pose 可能决定展开方向，当前静止帧不一定是正确装配姿势。

## 5. 当前安装规则

### 5.1 武器 dummy 映射

当前代码使用：

```text
LeftHandWeapon  -> Arms _dmy_23
RightHandWeapon -> Arms _dmy_20
LeftBackWeapon  -> Core _dmy_13
RightBackWeapon -> Core _dmy_14
```

位置：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:721
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:728
```

所有四个武器槽统一使用：

```csharp
localEulerAngles = new Vector3(180f, 0f, 0f);
```

位置：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:1007
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:1033
```

该规则来自对 DSAnimStudio `NewChrAsm.DoWPN` 的现有解读，但截图说明至少还有一层语义没有恢复。需要重新核对的不是单一角度，而是完整矩阵链：

```text
FLVER dummy reference transform
  -> ParentBone / AttachBone 当前姿势
  -> FromSoftware RH 坐标
  -> glTF RH 坐标
  -> glTFast Unity LH 坐标
  -> weapon install RotationX(PI)
  -> 武器自身 skeleton rest pose
  -> 武器默认动作或部署动作
```

### 5.2 当前验证器只检查 dummy 编号

验证器会确认武器是否位于预期 suffix 的节点下：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:402
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:416
```

推进器只确认：

- 找到三个 `_dmy_800`。
- 生成三个可见实例。
- 实例来自 NativeEquipment 路径。

位置：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:386
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs:401
```

这解释了为什么错误模型仍然可以通过验证。

## 6. 希望咨询者回答的问题

### 6.1 手持武器

1. AC6 的 `WepAbsorpPos`、dummy 20/23/25 与左右手、枪械/近战的最终映射是什么？
2. `NewChrAsm.DoWPN` 的 `RotationX(PI) * dummyCurrent` 在 XNA 行向量约定下，转换成 Unity 父子局部 TRS 时应该放在 dummy 左侧还是右侧？
3. glTFast 完成 RH 到 LH 转换后，是否还需要对左右手槽或武器根做额外镜像、四元数共轭或旋转顺序调整？
4. 武器模型原点是否真的就是握持安装点？是否存在 FLVER bone、dummy、`WepAbsorpPosParam` 字段或 skeleton rest transform 作为源握把锚点？
5. 手持武器是否必须先采样一个默认 HKX 动作，才能进入正确的握持姿势？
6. 近战武器与枪械是否实际使用不同 dummy 或不同安装矩阵？

### 6.2 背部武器与支架

1. AC6 背部武器的支架或导轨究竟属于武器 FLVER、核心 FLVER、独立 FLVER，还是运行时生成对象？
2. `17xxx/19xxx` rail slot 与核心 `2013/2014` dummy 各自表示什么状态？收纳和展开时如何切换？
3. 支架是否由 TAE 事件、行为脚本或武器 HKX 动作驱动？
4. `WP_R_4410`、`WP_L_4210`、`WP_R_4210` 的 mesh、骨骼和 dummy 中，哪些节点属于支架机构？
5. 背部武器 bind pose 是否本来就是折叠状态？样例应播放哪个默认动作才能得到游戏内静止姿势？
6. 如何从武器自身的枪口 dummy 判断最终前向，并自动发现当前朝向反转？

### 6.3 推进器

1. 核心上的 ref 800 dummy 是完整 Booster 安装点、单个喷口几何安装点，还是喷焰/SFX 发射点？
2. 一个 Booster FLVER 应实例化一次还是按每个 ref 800 实例化多次？
3. 如果只实例化一次，Booster 自身多个喷口如何与核心多个 ref 800 建立约束？
4. 如果需要实例化多次，应选择 Booster 内哪个子 mesh 或骨，而不是复制完整模型？
5. 不同核心的 ref 800 数量、顺序、Forward/Upward 和语义是否一致？
6. AC6 游戏或社区工具中是否存在未被 DSAnimStudio 实现的 Booster 装配参考代码？

## 7. 建议提供给咨询者的诊断数据

为了避免继续凭截图试角度，建议下一轮导出以下可复核数据：

1. 每个装备实例的 slot、PartId、模型路径、目标 dummy 全路径。
2. 目标 dummy 的 FLVER 原始 `Position/Forward/Upward`、ParentBone、AttachBone、Flag1。
3. dummy 的 bind pose 世界矩阵、动作中点世界矩阵和 Unity 最终世界矩阵。
4. 武器根、武器骨架根、枪口 dummy、握把候选骨的矩阵。
5. 背部武器全部 mesh 名称、bone palette、骨骼层级和默认可见状态。
6. 三种核心 1010/2010/3010 的全部 ref 800 记录，包括顺序、位置和朝向。
7. Booster GLB 内全部 mesh、骨骼、dummy 和 4 个动作的首帧/中点姿势。
8. 同一装备在原游戏或 DSAnimStudio 中的近距离正面、侧面、背面参考图。

## 8. 验证器需要增加的语义断言

### 8.1 手持武器

- 明确记录左手或右手 slot，禁止仅凭画面判断。
- 定义手掌握持锚点与武器握把锚点，检查位置和旋转误差。
- 用枪口 dummy 或刀刃轴计算前向点积，检测 180 度反向。
- 检查武器与手掌、前臂的异常穿透，不再用总 AABB 相交代替。
- 静止姿势和至少一个动作中点都必须通过。

### 8.2 背部武器

- 定义支架链路，验证核心挂点到武器主体之间存在预期 mesh 或骨骼机构。
- 分别验证收纳和部署状态。
- 用枪口 dummy 检查发射方向。
- 检查武器主体与核心的间隙、穿透和悬空距离。

### 8.3 推进器

- 对每个核心建立独立 ref 800 金标，位置和旋转都要验证。
- 检查喷口后向轴，而不只检查节点位置。
- 验证 Booster 是整体一次实例化还是正确的子组件多实例化。
- 检查每个视觉实例对应的喷口 ID，禁止仅按列表顺序碰巧匹配。
- 检查喷口不悬空、不埋入核心，并能跟随核心和自身动作。

## 9. 验收标准

只有同时满足以下条件，才能宣布装备挂载管线闭环：

1. 手持武器位于正确侧，握把与手掌对齐，枪口或刀刃方向正确，不与手掌、前臂严重穿插。
2. 背部武器方向与原游戏一致，并通过可见支架、导轨或经证实的原生连接结构与核心相连。
3. 背部武器在收纳、展开和自身动作期间保持正确连接。
4. 1010、2010、3010 三种核心分别使用自己的喷口位置和方向，不以未经验证的通用三实例方案代替。
5. 五套 loadout 在静止姿势和动作中点都通过数值断言与近距离人工检查。
6. 验证报告能输出具体 slot、PartId、dummy、握把/枪口/喷口误差，失败时可以直接定位原因。

## 10. 相关代码与资料

```text
Docs/AC6原生装备挂点问题_咨询日志.md
Docs/AC6生素材导入管线.md

Tools/DotNet/AC6PartToGltf/Program.cs
Tools/BuildAC6NativeEquipmentSamples.ps1

Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs
Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs
Assets/_Project/Scripts/Mech/Runtime/MechEquipmentAnimationPlayer.cs

Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/skinned_modular_validation.txt
```

## 11. 给下一位处理者的直接提醒

- 不要继续用“肉眼试一个欧拉角”解决全部装备。
- 不要把“挂到正确 dummy”当作“握持和方向正确”。
- 不要把“包围盒距离为 0”当作“没有穿模”。
- 不要把“动作中安装根不漂移”当作“安装根本身正确”。
- 不要恢复 Blender 烘焙武器，当前问题发生在原生资产的安装语义，不是纹理或材质管线。
- 优先确认截图中具体 PartId 和 slot，再比较 DSAnimStudio 或原游戏中的完整矩阵链。

## 12. 处理结果(2026-07-11,数据驱动回归后)

### 12.1 三个问题的答案

1. **手持武器反向**:根因是上一轮从 DSAS 照搬的 `RotationX(π)`。本轮用数据重推:机体面朝模型空间 **-Z**(HD ref=14 镜头 dummy 位于脸前 z=-6.66 处回望头部、眼部 SFX 朝 -Z),所有实测枪口/发射管都在武器自身 -Z(0010 dmy_10、4210 dmy_300/301/302、4410 dmy_200/300),挂点 CreateWorld 坐标系 ≈ identity——**正确安装 = identity,无任何旋转**。DSAS 的 RotX(π) 属于其渲染栈自身的约定补偿,不可照搬。回归实测:背武器发射管与机体正前点积 = 1.000,步枪 0.798(绑定姿态自然下垂)。
2. **近战 vs 枪械挂载口**:确认 `WepAbsorpPos 100→3023(左手掌 dmy_23)/ 200→3025(左前臂 dmy_25)`。AM FLVER 实测存在 dmy_25(attach=L_ForeArm)。0230(laserSFX 骨)、0250(MainBrade 骨)按近战重分类,经 mount tags(requiredTags/blockedTags="melee")+ `MechAssembler.ResolveParentMounts` 新增的 `CanAccept` 过滤挂到 dmy_25。截图确认刃单元贴合前臂、刃身朝前。
3. **背部支架与喷口**:支架**两端都在原数据里**——武器侧是 FLVER 内的机构骨链(WP_R_4210_05..09 + LookAt_01 瞄准枢轴),核心侧 2010/3010 的 dmy_13/14 直接挂在核心自己的 AP 机构骨(AP_BD_2010_021/022、AP_BD_3010_001)上,随核心机构动画。喷口:每核心 3 个 dmy_800(前胸刹车喷口 fwd -Z、背部两主推 fwd +Z),朝向编码在每个 socket 自身坐标系里,identity 安装即正确;1010/2010/3010 各自的位置已做成 per-core 金标断言。

### 12.2 新增语义断言(§8 的落地)

- 枪口方向断言:用枪口 dummy 自身朝向(-Z 轴)与机体正前的点积,静止时 >0.1(反向≈-1、侧向≈0)。注意 **ref=10 位于机匣不在枪管尖端,位置差不可用**。
- 每装备诊断行:slot / PartId / 挂点全路径 / muzzleDir / forwardness 写入报告。
- 近距截图:每装备 × 静止/动作中点,输出到 Reports/Closeup_*.png。
- per-core 金标:三种核心的 dmy_13/14/800 绑定位各自断言(金标必须在未动画的原始 GLB 实例上测)。

### 12.3 仍待办(需游戏 partsbnd 的机器)

- 背武器收纳/展开动作与 rail(17xxx/19xxx)状态机;近战默认持握动作采样;装备原生动作恢复后重跑(manifest 驱动的动作数断言会自动生效)。
- 截图仍会拍到相邻机体与标签,可后续加 layer culling。
