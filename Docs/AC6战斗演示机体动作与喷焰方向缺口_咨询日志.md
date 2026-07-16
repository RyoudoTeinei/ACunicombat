# AC6 战斗演示机体动作与喷焰方向缺口咨询日志

更新日期：2026-07-17

## 1. 结论先行

`AC6CombatShowcase` 当前只能证明七个阶段按时进入、装备动作播放器被调用、导弹对象被生成。它不能证明机体播放了对应动作，也不能证明尾焰方向正确。

Unity 实际画面确认了三个问题：

1. `ForwardFlight` 和 `QuickBoost` 阶段主要表现为整个机体根节点平移、俯仰，机体骨骼没有切换到明显的飞行或 QB 姿态。
2. `RangedWeaponFire` 阶段武器机构会动，但机体没有抬臂、持枪或跟踪目标的动作。
3. 两个蓝色诊断喷焰疑似朝向机体内部，方向与推进器排气方向相反。

![机体未抬臂且喷焰方向可疑](Images/ac6_showcase_body_animation_and_exhaust_direction_2026-07-16.png)

因此，旧日志 `AC6战斗演示与材质验证日志_2026-07-15.md` 中“飞行、QB、远程开火已经通过”的结论已撤回。

## 2. 复现步骤

1. 打开 `Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6CombatShowcase.unity`。
2. 进入 Play。
3. 观察 `ForwardFlight`、`QuickBoost` 和 `RangedWeaponFire` 阶段。
4. 从机体侧后方观察两个推进器 `_dmy_31` 下的蓝色粒子。

当前每个阶段约 4 秒，七阶段循环。

## 3. 已确认的代码根因

### 3.1 机体始终只有一组 AnimationClip

`AC6CombatShowcaseBuilder.cs` 的 `AnimationPath` 固定为：

```text
Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb
```

构建器只取该 GLB 中最长的一条 Clip，并用它装配机体。当前这条动作是 `a000_010900`。

模块化框架的 12 个 GLB 也都只包含同一动作对应的部件 Clip。当前没有为飞行、QB、持枪、抬臂或瞄准动作生成按部件适配的 Clip 集合。

### 3.2 运行时没有机体动作切换接口

`MechSynchronizedAnimationPlayer` 只保存一组：

```text
partAnimators + partClips + sharedBoneBindings
```

`Play()` 只能启动当前这一组 Clip。播放器没有 `actionId -> 四个框架部件 Clip` 的动作表，也没有运行时切换、混合或上半身叠加接口。

`AC6CombatShowcaseController.EnterPhase()` 在所有阶段都没有切换机体动作。它只切换推进器或武器自己的 `MechEquipmentAnimationPlayer`。

### 3.3 当前“飞行”主要是根节点轨迹

`ApplyMotion()` 直接修改：

```csharp
mech.transform.position = origin + localOffset;
mech.transform.rotation = Quaternion.Euler(pitch, 0f, 0f);
```

这可以作为游戏物理或演示轨迹，但不能代替飞行动作。当前画面中的“飞行”实际是 `a000_010900` 持续循环，同时整个对象被平移和俯仰。

### 3.4 远程开火只驱动武器

`FireRightHandWeapon()` 只调用右手装备播放器：

```csharp
rightHandWeaponPlayer.PlayAction("a000_800210");
```

它没有通知 `MechSynchronizedAnimationPlayer`，也没有上半身瞄准层。因此武器机构可以展开或后坐，但手臂不会抬起，枪口也没有由机体骨骼跟踪目标。

### 3.5 喷焰方向被硬编码旋转

`CreateExhaust()` 把粒子对象挂到推进器 `_dmy_31` 后，固定执行：

```csharp
root.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
```

现有原生 GLB 已经做过 AC6 到 glTF/Unity 的轴转换，dummy 自身也有旋转。这里再次固定旋转 180 度可能造成二次反转。

目前代码没有检查：

- 粒子最终世界发射轴是否远离核心。
- 尾焰方向是否与推进力方向相反。
- `_dmy_31` 的 AC6 原始 `Forward/Upward` 在 glTF/Unity 中对应 `+Z` 还是 `-Z`。
- 不同推进器和不同核心是否需要同一额外旋转。

### 3.6 自动验证是误报

`AC6CombatShowcaseBuilder.RunBatchValidation()` 当前只检查：

- 七条阶段日志是否出现。
- 武器开火日志是否出现。
- 是否生成导弹。
- 是否找到装备、材质和喷焰对象。

它没有测量任何机体骨骼姿态变化，也没有测量粒子的世界发射方向。因此旧结果 `RESULT=PASS` 是流程级通过，不是视觉和动画级通过。

## 4. 已确认的资源现状

完整生素材目录中存在大量机体动作：

```text
E:/UnityDev/Resources/AC6_ExtractedAssets/chr/AC - c0000/a000_*.hkx
```

可用动画 compendium 已找到：

```text
E:/UnityDev/tmp/ac6_anim_tests/from_game_c0000/7000000_a000_00.compendium
```

但当前 Unity 本地案例只复制、转换并提交了：

```text
a000_000000.hkx
a000_010900.hkx
Skeleton.hkx
```

所以问题不是“游戏没有这些动作”，而是尚未完成动作编号语义确认、多动作批量导出、模块化部件适配和运行时切换。

## 5. 需要咨询和解决的问题

### 5.1 动作编号与状态机语义

需要从原版 TAE、DSAnimStudio 或状态机数据确认至少以下机体动作的准确 ID：

1. 稳态前向飞行或空中移动循环。
2. Quick Boost 的开始、循环、结束，以及方向变体。
3. 右手枪械持枪、抬臂、瞄准、开火、后坐和恢复。
4. 移动中瞄准与开火时，哪些是全身动作，哪些是上半身叠加动作。
5. 背部导弹发射时是否有机体支撑或姿态动作。

不能只根据文件名范围猜动作含义。希望给出动作 ID、用途、是否循环、适用武器类别及需要的 TAE 事件。

### 5.2 多动作模块化导出方式

当前 `AC6PartToGltf` 已能把一条 HKX pose JSON 适配到每个头、核心、手臂、腿部的静止 bind pose。需要确定更合理的长期结构：

1. 每个框架部件 GLB 内嵌多条动作 Clip；或
2. 模型 GLB 只保留骨架和蒙皮，每条动作独立生成 Unity `.clip`；或
3. 建立统一 canonical rig Clip，再在 Unity 运行时重定向到四个模块化部件。

目标是避免每新增一个动作就复制 12 份完整网格，同时保持 34 根接口骨同步和任意换装兼容。

### 5.3 运行时动作切换与分层

希望给出适合当前 `PlayableGraph` 架构的方案：

- `actionId -> 每个槽位对应 Clip` 的动作注册表。
- 全身基础动作切换和交叉淡化。
- 飞行基础层上叠加上半身瞄准/开火。
- 武器自身 HKX 与机体动作使用同一时间基准。
- 换装后无需重新写 Animator Controller。

### 5.4 推进器 dummy 与喷焰轴

需要确认 `_dmy_31` 在 AC6 推进器 FLVER 中的准确语义及轴约定：

- 它是安装点、排气点还是特效点？
- 原始 `Forward/Upward` 如何映射到 glTF 和 Unity？
- 粒子应该沿 dummy 的 `+Z`、`-Z` 还是其他轴发射？
- 经过 `AC6PartToGltf` 后是否还需要额外 180 度旋转？

希望基于原始 dummy 数据或 DSAS/引擎语义回答，不使用“看起来差不多”的手工角度。

## 6. 建议的修复边界

本轮不接受以下权宜方案：

- 用根节点位移冒充飞行动作。
- 用程序化摆臂冒充已有原版 HKX。
- 为每个武器或机体手工写欧拉角。
- 重新启用 Blender 烘焙管线。
- 只检查日志、Transform 数值或对象数量就宣称视觉通过。

可接受的修复应继续使用原生 FLVER/HKX/DDS/glTF 管线，并让模块化换装机体真正播放对应原动作。

## 7. 最低验收标准

1. `ForwardFlight` 阶段至少一组核心、手臂或腿部骨骼相对悬浮姿态产生可测量旋转变化，而不是只有根节点移动。
2. `QuickBoost` 使用已确认的原版机体动作，开始和恢复阶段可见且可测。
3. `RangedWeaponFire` 中右臂或上身进入正确持枪/瞄准姿态，武器动作与机体动作同步。
4. 移动中开火时，基础飞行动作和上半身动作可以叠加或按原版规则切换。
5. 两个主喷焰的最终世界方向远离机体，并与推进力方向相反。
6. 更换 1010、2010、3010 核心和推进器后，喷焰自动使用各自 dummy，不依赖硬编码局部欧拉角。
7. 自动测试新增骨骼姿态差、动作 ID、播放时间和喷焰世界轴断言；任何一项不满足必须 `FAIL`。
8. Scene/Game 视图至少保存飞行、QB、抬臂开火和推进器侧后方四张验证截图。

## 8. 当前相关文件

```text
Assets/_Project/Scripts/Mech/Runtime/AC6CombatShowcaseController.cs
Assets/_Project/Scripts/Mech/Runtime/MechSynchronizedAnimationPlayer.cs
Assets/_Project/Scripts/Mech/Runtime/MechEquipmentAnimationPlayer.cs
Assets/_Project/Scripts/Mech/Runtime/MechActionEffectPlayer.cs
Assets/_Project/Editor/AC6CombatShowcaseBuilder.cs
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs
Tools/DotNet/AC6HkxPoseExporter
Tools/DotNet/AC6PartToGltf
Docs/AC6生素材导入管线.md
Docs/AC6挂点与动画全量参考.md
```

## 9. 咨询结论（2026-07-17，开发机）

逐条回应第 5 节。注意：开发机上 `Raw/` 目录（gitignored）里的 GLB 是 a1bdac6 修复之前的旧产物，本节 5.4 的结论不依赖它们，而是从已入库且在游戏机上验证通过的代码语义反推，并附可复跑的数值工具。

### 9.1 问题 5.4 的裁决：喷焰沿 `_dmy_31` 的 +Z（transform.forward）发射，删除硬编码 180°

三个事实链条：

1. **语义已由座面修复定死**：a1bdac6/6d56a97 之后，`ref 800` 是模型安装 socket（推进器 root 直接坐上去、继承其帧），`_dmy_31/41/51` 是排气/特效锚点——builder 注释与断言（`AC6SkinnedModularSampleBuilder.cs:446-448、480`）已经写明。
2. **验证器现在断言 `exhaustDir = flame.forward`（正号，`:480`）指向后方且不向上，并在重新生成的 GLB 上通过**——即修复后的数据里，`_dmy_31` 的 +Z 世界方向就是排气方向。
3. **导出器修复（a1bdac6 对 `Program.cs` 的 `-forward`）消除了旧 GLB 中 dummy 帧的 180° 掉头**。`CreateExhaust()` 里的 `Quaternion.Euler(0f, 180f, 0f)` 正是对旧翻转数据的手工补偿——在新数据上它变成二次反转，把喷焰打进机体内部，与截图现象一致。

修复方式：

- 删除 `CreateExhaust()` 的硬编码 180°，粒子对象 localRotation = identity，发射轴用父 dummy 的 +Z（ParticleSystem 本地 +Z 即 `transform.forward`）。
- 不同推进器/不同核心**不需要**任何额外旋转：socket 帧 + 原生 dummy 已携带全部朝向（金标验证已跨 1010/2010/3010 证明）。
- 按第 7 节第 5/7 条补世界轴断言（伪代码）：

```text
exhaust = flameDummy.forward
Check( Dot(exhaust, mech.forward) > 0.6 )                        // 指向后方（mech faces -Z）
Check( Dot(exhaust, (flameDummy.pos - core.pos).normalized) > 0 ) // 远离核心，而非射入机体
对每个粒子对象断言其世界发射轴与 exhaust 夹角 < 5°
```

- 数值复核工具已入库：`Tools/GlbDummyProbe.py`。在游戏机上对重新生成的 GLB 运行：

```text
python Tools/GlbDummyProbe.py <BS_A_*.glb> dmy --anim 000001
python Tools/GlbDummyProbe.py <BD_M_*.glb> _dmy_800
```

预期：修复后推进器 dummy rest fwd 与核心后方 socket fwd 同为 +Z（glTF 空间=向后），`a000_000001` 第 0 帧 pitch ≈ −19.97°（后下）。若任一符号不符，先修数据再动代码。

### 9.2 问题 5.2 的建议：动作与网格分离，短期方案 2、长期方案 3，明确否决方案 1

- **否决方案 1**（每部件 GLB 内嵌多条 Clip）：每加一个动作复制 12 份网格容器，资源体积和重导出成本线性爆炸，且动作与模型版本互相锁死。
- **短期（解本次缺口）用方案 2**：模型 GLB 只含骨架+蒙皮（一次性）；每条动作走现有 `HKX → pose JSON → 按部件 bind pose 适配` 链路，但产物是独立的 Unity `.clip` 资产（每动作 × 4 框架部件），由批量导入器生成。现有适配代码全部复用，只是把"取 GLB 里最长的 Clip"改成"从动作库按 actionId 取"。
- **长期收敛到方案 3**（canonical rig）：这本来就是资源导入咨询定下的架构——共享骨架实例 + 按名重绑 `SkinnedMeshRenderer.bones`。完全统一后，每条动作只需一条针对 canonical 骨架路径的 Clip（n_动作 × 1 而非 × 4），34 根接口骨同步问题自然消失，因为四个部件蒙的就是同一副骨架。方案 2 的动作库数据结构应按"未来每动作只剩一条 Clip"设计，迁移时只删适配层。

### 9.3 问题 5.3 的建议：动作注册表 + 双层 LayerMixer 的 PlayableGraph

数据层（ScriptableObject，换装不改代码）：

```text
MechActionRegistry:
  actionId → { 每槽位 Clip 集（方案2）或 canonical Clip（方案3）,
               loop, layer(FullBody/UpperBody), blendIn/blendOut 秒,
               效果时间线引用（现有 MechActionEffectTimeline） }
```

图结构（每机体一个 PlayableGraph，装备播放器并入同图共享时钟）：

```text
AnimationLayerMixerPlayable
 ├─ Layer 0（全身基础）：双槽 AnimationMixerPlayable，当前/下一动作交叉淡化
 │    （权重按图时间推进，禁用 Time.deltaTime 累加，保证与装备 HKX 同基准）
 └─ Layer 1（上半身叠加）：持枪/瞄准/开火，AvatarMask 用共享骨架的
      脊柱+双臂 transform 路径列表构建（Generic 骨架用 AddTransformPath，
      不需要 humanoid Avatar）；weight 0→1 淡入，开火结束淡出
```

- 换装后重建 graph 输出绑定即可，没有 Animator Controller 资产。
- 瞄准跟踪（枪口指向目标）不属于动作层：在 LateUpdate/Playables 后处理做上臂链 IK 微调，叠加在 Layer 1 姿态之上，幅度受限（±15° 级），超限由火控转身逻辑接管——与《信号级传感器与火控仿真_设计实现.md》§18 的姿态控制责任划分一致。
- 关于 3.3 的定性：根节点轨迹**不是要移除的权宜**——按传感器/火控实现基准，运动权威本来就在运动系统，动作永远只提供姿态。缺的是"飞行姿态 Clip"这一层，不是"根运动"那一层。验收第 1 条应据此理解：根节点继续由控制器/物理驱动，同时骨骼有姿态变化。

### 9.4 问题 5.1 的回答方式：不猜 ID，给出可执行的判定流程

动作 ID 语义必须在游戏机上从数据判定，流程按证据强度排序：

1. **TAE 是权威源**：`tae_action/b000.tae` 用 SoulsFormats 的 TAE 类直接解析（AC6 与 ER 同代格式），每个 Animation 条目携带 ID 与 mini-header（含 `ImportsHKX/AllowDelayLoad/引用动画文件 ID`）——这同时解掉旧咨询遗留的"TAE 显示名 vs binder 内 HKX 文件"的 alias 映射问题（推进器 TAE 同法处理）。输出表列：`TAE anim ID | 引用 HKX | 循环标志 | 事件摘要（SFX/状态/判定帧）`。
2. **根运动剖面分类**：用现有 AC6HkxPoseExporter 批量导出候选动作的根骨曲线，按运动学特征自动分桶——持续前向速度=飞行循环；0.2–0.4 s 横向冲量+快速衰减=QB 变体（按冲量方向分左右前后）；根几乎静止+手臂链大幅旋转=持枪/瞄准/开火族。分桶结果回填第 1 步的表。
3. **DSAnimStudio 目检**只做最后确认（第 1、2 步筛出的候选每类看 2–3 条），不做地毯式翻找。
4. 上半身叠加判定：TAE 事件里带部分骨骼混合标记、或根骨+腿链曲线为空/恒定而脊柱以上有曲线的动作即叠加型——第 2 步的曲线数据可以直接判，不需要猜。

`ThrustersLocomotionParam_PC` 的未知整数字段建议与第 1 步产物对照：若字段值命中 TAE anim ID 集合，即为状态→动作路由表，QB/飞行的官方入口就从这里读。

### 9.5 建议的实施顺序

1. 喷焰轴修复 + 世界轴断言（9.1，半天，独立交付）；
2. TAE 解析与动作判定表（9.4，游戏机，1 天）；
3. 动作库批量导出（9.2 方案 2）+ 注册表与双层 graph（9.3），先接 ForwardFlight/QB/持枪三个动作族打通验收 1–4；
4. 骨骼姿态差与喷焰世界轴自动断言（验收 7），截图归档（验收 8）。
