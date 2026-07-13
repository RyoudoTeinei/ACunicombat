# AC6 装备轴回归与推进器安装修正实施日志

日期：2026-07-14

## 1. 现象

在提交 `a1bdac6` 修正 FLVER dummy 轴导出后，Unity 验证场景出现两类明显回归：

1. 左右手武器、近战武器和左右背部武器整体反向或安装姿态错误。
2. 推进器虽然通过了 `flameToSocket=0`，可见喷口却被整体推出核心，在背部悬空，且三种核心的错位量不同。

这说明旧验证把“某个 dummy 数值重合”误当成了“可见装备安装正确”。

## 2. 武器回归根因

`AC6PartToGltf` 已改为：

```csharp
Matrix4x4.CreateWorld(Vector3.Zero, -forward, upward)
```

该修改正确地把 FLVER `Forward` 保存为 glTF dummy 的局部 `+Z`，但 Unity 侧武器仍沿用了旧版错误 dummy 轴下的 identity 安装。结果是 socket 坐标系修正后，武器模型相对它整体转反。

修正方式不是撤销导出器，而是在原生武器模型与 socket 的安装边界统一补偿：

```text
LocalPosition = (0, 0, 0)
LocalEulerAngles = (0, 180, 0)
LocalScale = (1, 1, 1)
```

验证器同时新增以下硬断言：

- 四种武器槽位都必须使用该安装旋转；
- 左右手、近战和左右背部武器在静止姿态下，枪口或工作端必须明确朝向机体前方；
- 近战武器不再被方向检查豁免。

## 3. 推进器错位根因

旧实现采样推进器动作后读取 `_dmy_31`，再使用：

```text
installOffset = -position(_dmy_31)
```

目的是强行让推进器 `_dmy_31` 与核心后部 `ref=800` 重合。这个前提错误：

- 核心后部 `ref=800` 是当前推进器实例的安装 socket；
- 推进器资源根是安装原点；
- `_dmy_31/41/51` 是推进器内部随机构运动的排焰/特效点，不是模型安装原点。

旧算法因此把整个模型向外平移了排焰点到资源根的距离。旧报告中的根偏移已经直接暴露问题：

```text
1010: (0, -0.194, 0.491)
2010: (0, -0.177, 0.452)
3010: (0, -0.427, 0.936)
```

3010 的悬空最明显，正好对应最大的错误偏移。

修正后的安装契约为：

```text
booster resource root -> selected core rear ref 800
local position        = (0, 0, 0)
local rotation        = Euler(180, 0, 0)
local scale           = (1, 1, 1)
```

核心前方的效果用 `ref=800` 仍由 `sourceRegion: Rear` 排除。每台机体只实例化两个后部推进器。推进器的 `a000_000001` 第 0 帧只负责机构的后下展示姿态，不负责模型根的安装平移。可见模型在原生装备边界还需要绕 X 轴半转；若保持 identity，喷嘴出口会朝向核心，模型实际处于前后反装状态。

## 4. 验证修正

旧通过条件：

```text
flameToSocket < 5 mm
```

已废除。新通过条件：

```text
rootToSocket < 5 mm
root rotation error against Euler(180, 0, 0) < 0.05 deg
root scale error < 0.0001
```

`flameFromSocket` 继续写入报告，但只作为排焰点诊断，不参与模型安装通过判定。修正后的实测值：

```text
1010 rootToSocket=0m, flameFromSocket=0.5281m
2010 rootToSocket=0m, flameFromSocket=0.4856m
3010 rootToSocket=0m, flameFromSocket=1.0287m
```

这些非零焰点距离是资源内部结构差异，不是安装误差。

排气方向必须直接使用 `flame.forward`。旧实现使用 `-flame.forward`，恰好把“模型反装”和“验证轴取反”两个错误抵消，因此即使报告得到后下方向，可见喷嘴仍然朝向核心。现在验证器检查真实正向，并配合 X 轴半转断言模型安装姿态。

## 5. Unity 实测结果

使用已打开的 Unity 6000.5.2f1 重建 `AC6SkinnedModularValidation.unity`：

```text
5/5 loadout PASS
每台可见后推进器 = 2
全部推进器 rootToSocket = 0m
全部推进器排气轴 backDot = 0.940
全部推进器展示俯角 = -19.97 deg
手持武器静止 forwardness = 0.798 .. 0.802
背部武器静止 forwardness = 1.000
```

这里的 `exhaustDir=(0,-0.342,+0.940)` 来自实际的 `+flame.forward`，不再是对反装模型取负号得到的伪正确结果。

近景图位于本机目录：

```text
Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/
```

重点文件为 `Closeup_*_Boosters_t000.png`、`Closeup_*_LeftHandWeapon_t000.png`、`Closeup_*_RightHandWeapon_t000.png`、`Closeup_*_LeftBackWeapon_t000.png` 和 `Closeup_*_RightBackWeapon_t000.png`。重建后的 1010、2010、3010 推进器基座均由对应核心包住，不再悬空；四个武器槽位与近战工作端恢复朝前。

## 6. 管线边界

本轮继续使用原生 FLVER/HKX/DDS -> glTF/Unity 管线，没有恢复 Blender 烘焙资源，也没有在导出文件里手工烘死装备姿态。
