# AC6 推进器可视朝向与动作特效验证缺口咨询日志

更新日期：2026-07-13

## 1. 本轮结论

当前问题仍未解决。

最新代码已经做到：

- 只在核心后方两个 `_dmy_800` 下实例化可见推进器。
- 核心前方的 `_dmy_800` 保留在原始层级中，但不再生成推进器模型，预留给制动或其他 SFX。
- 1010、2010、3010 分别使用各自核心的后方挂点位置。
- 从推进器原始动作 `a000_000001` 的根旋转曲线首帧读取约 `19.972°`，并写入推进器外层实例。
- 旋转后重新计算推进器 `_dmy_31` 到核心 `_dmy_800` 的位置对齐。

自动验证显示 5/5 loadout 通过，两个实例的 `flameToSocket` 误差为 0 到 2 mm。然而 Unity 中的实际网格仍接近水平，安装观感与预期基本没有变化。

因此当前 `5/5 PASS` 只能证明对象数量、挂点位置、外层 Transform 数值和动画跟随关系满足断言，不能证明最终可见推进器的朝向、嵌入深度或结构语义正确。

本轮实际失败画面：

![推进器仍然接近水平](Images/ac6_booster_still_horizontal_2026-07-13.png)

## 2. 复现方法

Unity 菜单：

```text
Tools > ACunicombat > AC6 > Build And Validate Skinned Modular Samples
```

验证场景：

```text
Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity
```

从机体侧后方观察任意 1010、2010 或 3010 核心。当前可见推进器数量已经从三个减少到两个，但喷管最终几何轴仍接近水平，没有呈现预期的斜向下安装。

## 3. 本轮实现及其局限

### 3.1 后方挂点筛选

新增 `MechMountSourceRegion`，允许挂点定义限定为 `Front` 或 `Rear`。装配器按挂点相对部件根节点的本地 Z 位置筛选源 Transform。

当前核心规则：

```text
suffix: _dmy_800
region: Rear
matchAll: true
```

这能够稳定得到两个后方挂点，并排除前方制动或特效点。

### 3.2 推进器安装角读取

构建器在 `a000_000001` 中寻找最靠近根层级的 `m_LocalRotation.x/y/z/w` 曲线，读取首帧四元数。当前三种推进器都得到约：

```text
localEulerAngles = (19.972157, 0, 0)
```

安装偏移按下式重新计算：

```text
flameAnchorLocal = inverse(modelRoot) * dmy31World
installOffset    = -(installRotation * flameAnchorLocal)
```

### 3.3 当前验证为何是假阳性

现有验证检查的是：

1. 后方 Booster 挂点数量等于 2。
2. 两个挂点的父节点名包含 `_dmy_800`。
3. 挂点在核心局部坐标的后半区。
4. 推进器外层 `visual.localRotation` 等于写入的安装角。
5. 推进器 `_dmy_31` 世界位置与核心 `_dmy_800` 世界位置重合。
6. 推进器跟随核心动画时不漂移。

这些条件都没有测量最终网格的喷管轴。只要外层包装对象是 19.972°，即使内部动画根、坐标转换节点或模型几何抵消了这个旋转，验证仍会通过。

另外，自动生成的 `Closeup_*_Boosters_*.png` 在本轮位置变化后经常把相机放进网格内部，也不能作为可靠的视觉回归证据。

## 4. 当前最可能的技术卡点

### 4.1 GLB 外层包装节点不等于动画目标根

`AssetDatabase.LoadAssetAtPath<GameObject>` 返回的是 Unity 导入后的 prefab 包装根。`a000_000001` 的旋转曲线可能实际绑定到内部 `BS_A_x010` 节点，而不是这个包装根。

当前实现把内部动画根的四元数写到了外层实例。数值相同不代表矩阵作用在同一坐标空间，也不保证最终喷管轴旋转相同。

需要明确以下层级：

```text
Unity imported prefab wrapper
  -> glTF scene root
    -> BS_A_x010 animation target
      -> skeleton / mesh roots
        -> dmy_31 / dmy_41 / dmy_51
```

并确认安装矩阵应该乘在哪一层、位于 bind pose 的左侧还是右侧。

### 4.2 19.972° 可能是动作姿态而非安装姿态

`a000_000001` 是工作、偏转或推力相关动作。它的首帧旋转不一定是静止安装角，也可能是动作本身的起始偏转。

当前三种推进器都得到相同角度，这既可能是共享正确基准，也可能只是共享动作模板，不能仅凭数值一致就认定正确。

### 4.3 `_dmy_31` 可能只定义 SFX 发射点

让 `_dmy_31` 与核心 `_dmy_800` 重合能保证尾焰点重合，但不能证明：

- 推进器网格应以 `_dmy_31` 为安装锚点。
- 可见喷管应该完整实例化两次。
- 推进器根到核心外壳的嵌入深度正确。
- `_dmy_41`、`_dmy_51` 与多喷管结构的语义已经恢复。

必须区分模型安装锚点、动画枢轴和 SFX 发射点。

### 4.4 缺少最终几何轴断言

验收必须测量最终世界空间中的喷管后向轴，而不是外层 `Transform.localRotation`。候选数据源：

1. 推进器 FLVER dummy 的原始 `Forward/Upward`。
2. `_dmy_31`、`_dmy_41`、`_dmy_51` 的世界旋转。
3. 两个明确的喷管骨或 dummy 之间形成的轴。
4. 已确认属于喷管的 mesh 顶点主轴，仅用于诊断，不应优先于原始语义数据。

验证应计算该轴与期望斜向下后方方向的点积，并在水平、反向或向上时失败。

## 5. 需要新增的动作测试矩阵

后续不能只测试一个 12 秒示例动作的 `t=0` 和 `t=6`。必须建立语义化动作 manifest，将原始 action ID、TAE 事件和游戏状态对应起来。

| 场景 | 机体动作 | 装备动作 | 必查内容 |
| --- | --- | --- | --- |
| 静止装配 | Idle / rest | 武器静止、推进器静止 | 所有部件连接正确，无漂移、穿插和错误展开 |
| 武器收纳 | Idle | 手持、背部、近战武器收纳 | 支架、导轨、折叠 mesh 和可见性正确 |
| 武器部署 | Idle 或飞行 | 收纳到部署 | 支架连续连接，动作结束后枪口方向正确 |
| 武器待机 | 持枪或战斗姿态 | Ready / hold | 手掌、前臂、背部武器保持正确接触 |
| 静止发射 | 战斗姿态 | Fire / recoil / recover | 枪口、后坐、弹仓或活动机构、发射事件同步 |
| 移动发射 | 飞行或横移 | Fire | 机体基础动作与武器动作能叠加，不互相覆盖 |
| 自适应瞄准发射 | 飞行并移动瞄准目标 | Aim / track / fire | 手臂、上身、炮塔、背部导轨按目标方向自适应，枪口始终指向目标 |
| 稳态飞行 | Flight loop | 推进器工作循环 | 核心、腿部和背部主喷口的姿态与尾焰持续正确 |
| 姿态调整 | 横移、转向、制动 | 推进器偏转 | 对应方向的 RCS 或制动喷口触发，错误方向的喷口不得点火 |
| QB 前后左右 | Quick Boost start / loop / end | QB 推进器动作 | 机体姿态、推进器偏转、爆发尾焰和结束恢复同步 |
| QB 上下 | 六自由度项目扩展动作 | QB 推进器动作 | 若原始 AC6 无对应动作，需要明确标记为重定向或程序化扩展 |
| 护盾部署 | Shield deploy | 护盾展开 | 模型、骨架和效果从正确挂点展开 |
| 护盾维持 | Shield hold | 护盾循环 | 效果面跟随装备，不随相机或世界漂移 |
| 护盾受击 | Hit reaction | Shield impact | 命中点、法线、局部波纹或闪光与碰撞位置一致 |
| 护盾解除 | Shield collapse / recover | 收纳或过热 | 效果和模型按正确时序关闭，无残留对象 |

### 5.1 自适应武器发射的最低测试要求

建立至少三个移动目标：

```text
TargetCenter: 正前方
TargetLeftHigh: 左上方移动
TargetRightLow: 右下方移动
```

机体同时进行前进飞行和横移，武器需要完成：

1. 目标跟踪。
2. 上身或手臂允许范围内的瞄准补偿。
3. 武器自身炮塔、瞄准框或导轨动作。
4. 枪口最终世界轴对准预测拦截点，而不是只对准目标当前位置。
5. 发射和后坐期间不丢失挂点约束。
6. 超出机械角度时进入明确的不可射击状态，而不是扭曲骨架。

## 6. 需要新增的特效测试矩阵

### 6.1 机体与推进器喷口

必须恢复并验证至少以下类别：

- 背部两个主推进器尾焰。
- 前向制动喷口。
- 左右横移或姿态控制喷口。
- 腿部、核心或肩部参与 QB 的爆发喷口。
- 推进器启动、持续、关闭三个时序状态。

每个效果实例必须记录：

```text
effectId
source part
source dummy / bone
world position
world forward
triggering state or TAE event
start and stop time
```

验收条件：

1. 效果只从语义正确的 dummy 发射。
2. 尾焰方向与推进力方向相反。
3. QB 时只激活与运动方向对应的喷口集合。
4. 效果不能从机体内部、前胸错误位置或已经排除的可见模型挂点产生。
5. 更换核心、腿和推进器后，效果自动使用新部件自己的 dummy。

### 6.2 武器发射特效

至少覆盖：

- 枪口火焰或能量闪光。
- 弹体、光束或导弹生成点。
- 曳光、烟雾或能量残留。
- 弹壳、排气或抛壳口，如果原始资源存在。
- 充能、发射、后坐、冷却的事件时序。
- 多枪管或多发射管逐管触发顺序。

发射特效必须绑定武器自己的 muzzle dummy，不能绑定机体手掌挂点。发射方向必须由最终动画姿态下的 muzzle 世界轴决定。

### 6.3 护盾特效

至少覆盖：

- 展开或启动特效。
- 持续护盾面。
- 命中点局部特效。
- 护盾破裂、过热或关闭特效。

必须确认护盾效果资源来自哪个 binder、SFX ID 或 TAE 事件，并确认其跟随的是护盾装备骨、手臂骨还是独立效果挂点。

## 7. 验证器需要升级的内容

### 7.1 从 Transform 数值改为最终结果断言

推进器：

- 可见实例数量必须符合部件语义。
- 最终喷管世界轴必须斜向后下方。
- 喷管与核心连接面距离和穿插深度必须在允许范围内。
- 前方效果 dummy 不得生成推进器网格。

武器：

- 最终枪口轴必须对准目标或预测拦截点。
- 发射帧的枪口特效位置必须和 muzzle dummy 重合。
- 后坐与恢复后不能改变安装根的长期偏移。

护盾：

- 护盾面法线、装备朝向和命中法线必须一致。
- 效果生命周期结束后不得残留实例。

### 7.2 增加状态时间点

每个动作至少采样：

```text
rest
state enter
25%
50%
75%
event frame
state exit
recovered rest
```

对循环动作至少采样两个循环，检测累计漂移。

### 7.3 改进截图

每台机体、每个关键状态自动生成：

- 正面全身。
- 左右侧面。
- 背面全身。
- 推进器局部。
- 左右手武器局部。
- 左右背部武器局部。
- 枪口或护盾效果局部。

截图相机应使用隔离 layer，只渲染当前机体和当前特效。相机距离由目标包围盒计算，并增加近裁剪保护，禁止相机进入网格内部。

## 8. 需要咨询者确认的数据

1. `BS_A_*` 的正确装配矩阵链，具体到 prefab 包装根、glTF scene root、动画根和 mesh 根。
2. 推进器的静止安装角来自 bind pose、哪条 HKX 动作，还是额外参数。
3. 核心 `_dmy_800` 与推进器 `_dmy_31/41/51` 的准确语义。
4. 一套 Booster 是一个整体实例、两个实例，还是由子 mesh 和效果组合出来。
5. `c0000` 中 Idle、持枪、飞行、QB、瞄准、开火和护盾状态对应的 action ID。
6. 武器独立 HKX 中收纳、部署、开火、后坐和恢复对应的 action ID。
7. 自适应瞄准由哪些骨骼、LookAt 节点、行为参数或程序化约束驱动。
8. TAE 中哪些事件触发推进器、枪口、弹体、护盾和命中特效。
9. 对应 SFX、behbnd、anibnd、parts 或 weapon binder 的具体资源路径和 ID 映射。
10. 是否有可参考的 DSAnimStudio、SoulsFormats、Smithbox 或其他社区实现。

## 9. 验收标准

只有同时满足以下条件，才能宣称动画与效果管线闭环：

1. 三种核心和两套混装都只显示正确数量的推进器，位置、朝向和嵌入深度人工检查通过。
2. 推进器最终喷管轴通过数值方向断言，而不是只检查外层 Transform。
3. 武器收纳、部署、待机、发射、后坐和恢复完整播放。
4. 飞行、姿态调整和 QB 动作与正确喷口特效同步。
5. 移动瞄准目标时，武器与机体动画能够自适应叠加，枪口对准预测拦截点。
6. 枪口、弹体、导弹、尾焰和护盾效果来自正确 dummy，并在正确事件帧启停。
7. 所有状态结束后恢复到稳定姿态，无部件漂移、累计偏移、穿插或残留特效。
8. 五套 loadout 的数值断言和固定视角截图都通过。
9. 全流程只使用 AC6 原生素材导入管线，不恢复 Blender 烘焙资产。

## 10. 相关代码与报告

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs
Assets/_Project/Scripts/Mech/Data/MechMountPointDefinition.cs
Assets/_Project/Scripts/Mech/Data/MechPartDefinition.cs
Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs
Assets/_Project/Scripts/Mech/Runtime/MechEquipmentAnimationPlayer.cs
Assets/_Project/Scripts/Mech/Runtime/MechSynchronizedAnimationPlayer.cs

Tools/DotNet/AC6PartToGltf/Program.cs
Tools/BuildAC6NativeEquipmentSamples.ps1

Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/skinned_modular_validation.txt
Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/Closeup_*_Boosters_*.png
```

## 11. 给下一位处理者的直接提醒

- 当前推进器问题不是 Unity 没刷新，最新失败截图来自已经重建的场景。
- 不要把 `flameToSocket = 0` 当作网格安装正确。
- 不要把外层 `localEulerAngles = 19.972°` 当作最终喷管轴已经改变。
- 在确认 GLB 内部动画目标层级前，不要继续尝试新的手调欧拉角。
- 先建立 action ID 和 TAE/SFX 事件的语义表，再实现状态机和特效触发。
- 所有新测试都必须同时包含数值断言和可复核截图。
