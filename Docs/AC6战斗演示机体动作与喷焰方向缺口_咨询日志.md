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
