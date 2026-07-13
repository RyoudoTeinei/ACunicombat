# AC6 核心差异化推进器接口与展示姿态咨询日志

日期：2026-07-13

基线提交：`77bc72a`

范围：只讨论核心背部推进器的安装位置、安装姿态、动作根骨和坐标转换。当前管线只使用 AC6 原生 FLVER/HKX/TAE 数据，不恢复 Blender 烘焙管线。

## 1. 本轮新增的决定性证据

用户在 AC6 游戏本体的“机体鉴赏”界面拍摄了六张原图。它们不是 Unity 复现图，而是当前问题的验收基准。

这些图可以确认：

1. 可见的主推进器只安装在核心背部左右两侧；核心前方或胸前不应出现一套可见推进器模型。
2. 不同核心的左右接口位置不同。有的靠近中轴并偏低，有的横向间距更大，有的位于更高或更外侧的位置。
3. 接口不仅提供位置，还形成不同的安装姿态。喷管总体朝后下方，但各核心的俯仰角、横向间距、嵌入深度和外壳贴合关系不同。
4. 六张图拍摄时间横跨约两分钟，喷管始终保持稳定的后下姿态。这不是随机播放到的某个摆动帧，应视为机库/鉴赏状态下的稳定展示姿态。

### 1.1 原图

![后侧接口 A](参考截图/AC6推进器接口_2026-07-13/20260713200725_后侧接口_A.jpg)

![侧后接口 A](参考截图/AC6推进器接口_2026-07-13/20260713200730_侧后接口_A.jpg)

![后侧接口 B](参考截图/AC6推进器接口_2026-07-13/20260713200748_后侧接口_B.jpg)

![侧后接口 B](参考截图/AC6推进器接口_2026-07-13/20260713200813_侧后接口_B.jpg)

![正后接口 C](参考截图/AC6推进器接口_2026-07-13/20260713200817_正后接口_C.jpg)

![斜后接口 C](参考截图/AC6推进器接口_2026-07-13/20260713200901_斜后接口_C.jpg)

## 2. 当前原生管线的实现

相关代码：

```text
Assets/_Project/Editor/AC6SkinnedModularSampleBuilder.cs
Assets/_Project/Scripts/Mech/Runtime/MechAssembler.cs
Tools/DotNet/AC6PartToGltf/Program.cs
Tools/BuildAC6NativeEquipmentSamples.ps1
```

当前装配规则：

1. 核心部件从原生 FLVER/glTF 中提供 `_dmy_800`。
2. 每个核心实测有三个 `ref 800`：一个位于前方，两个位于背部。装配器用 `sourceRegion: Rear` 过滤，只在背部两个点各实例化一套推进器模型。
3. 推进器产品模型来自 `BS_A_*`，不从核心网格中截取，也不使用 Blender 资产。
4. 推进器自身中央焰点 `_dmy_31` 与核心背部 `_dmy_800` 对齐。
5. 外层安装包装 Transform 保持 identity；推进器内部根骨和万向节由原生 HKX 动作驱动。

这个结构已经解决“核心前方多出一套完整推进器”的旧问题，但只证明焰点位置重合，不证明喷管朝向和外壳贴合正确。

## 3. 已确认的核心差异数据

原始核心 FLVER 中，左右背部 `_dmy_800` 的位置确实随核心变化。以下为原始坐标中的代表值：

| 核心 | 左右 X | Y | Z | Forward | Up |
| --- | ---: | ---: | ---: | --- | --- |
| `BD_M_1010` | `±0.413` | `8.944` | `2.402` | `+Z` | `+Y` |
| `BD_M_2010` | `±0.459` | `9.155` | `2.469` | `+Z` | `+Y` |
| `BD_M_3010` | `±0.459` | `9.472` | `2.469` | `+Z` | `+Y` |

结论：

- 核心原生 dummy 已经携带差异化位置，当前 Unity 管线可以得到这些位置差异。
- 但这三套后方 `ref 800` 的原始基向量都近似水平的 `Forward=+Z, Up=+Y`，没有直接给出游戏截图中的后下俯仰角。
- 因此不能简单宣称“直接使用 `_dmy_800` 完整矩阵即可闭环”。角度还来自另一个数据层，或当前坐标转换丢失了它。

## 4. HKX 动作与坐标矛盾

`BS_A_1010/2010/3010` 均导出了四条原生动作。已测结果：

| 动作 | 根骨表现 |
| --- | --- |
| `a000_000000` | 静态 idle，根骨 X 约为 `0°`，Unity 中喷管水平 |
| `a000_000001` | 361 帧万向/工作动作；第 0 帧根骨 X 为 `+19.972°` |
| `a000_000002` | 根骨初始约为 `0°` |
| `a000_000003` | 与工作/偏转相关，初始也有明显角度 |

`a000_000001` 第 0 帧的原始 pose JSON 根骨四元数为：

```json
{"x": 0.17340888, "y": 0, "z": 0, "w": 0.98484993}
```

对应约 `+19.972° X`。原始工具声明 Havok/FromSoft 与 glTF 都是右手、Y-up，导出器不主动做轴转换；glTFast 再将 glTF 导入 Unity。

### 4.1 已执行但已撤销的失败实验

曾临时把 `a000_000001` 第 0 帧当作机库展示姿态，在装配时采样一次，外层包装仍保持 identity。五套 loadout 的结果全部失败：

```text
flameToSocket = 0m
exhaustDir = (0, 0.342, 0.940)
backDot = 0.940
pitchDeg = +19.97°
Validation found 20 failure(s) across 0 passing loadouts.
```

它证明：

- 焰点仍精确落在核心 socket 上，所以点对点安装算法没有丢位置。
- 但最终喷管向上 `19.97°`，与游戏原图的后下姿态相反。
- 不能把该帧直接采样，也不能在外层手写 `-20°`。前者方向错误，后者会在播放原生动作时与内部根骨旋转叠加。

该实验代码已经从工作树撤销，本次提交不会把错误默认动作带给下一位处理者。

### 4.2 当前最重要的技术怀疑

这里至少有三种尚未裁决的可能：

1. `a000_000001` 确实是正确的机库/工作姿态，但装备根骨四元数经过 glTF/glTFast 左右手系转换后符号或乘法顺序错误，导致原本的“向下”变成“向上”。
2. `a000_000001` 不是机库展示动作。游戏行为层通过 TAE alias、BHV 状态或另一个动作 ID 驱动了稳定后下姿态；当前按 HKX 文件名生成 action ID 的映射不正确。
3. 核心 `_dmy_800` 只表示焰点或效果位置，真正的产品安装基准还需要另一个 dummy、部件参数或行为层矩阵；当前只把 `_dmy_31` 对到 `_dmy_800`，因此缺少“座面到座面”的完整约束。

## 5. 已排除或暂未找到的来源

已用 WitchyBND 解出并检查：

```text
ThrustersLocomotionParam_PC
ThrustersParam_PC
ThrustersSfxIdParam_PC
EquipParamBooster
BoostParam
MenuPartsModelRendParam
```

目前结论：

- `ThrustersParam_PC` 主要是状态到推进器 SFX 的路由。
- `EquipParamBooster` 主要是性能、模型 ID 等数据。
- `MenuPartsModelRendParam` 未发现可直接解释每个核心安装角的矩阵或欧拉角。
- `ThrustersLocomotionParam_PC` 有大量未知整数字段，可能指向行为/动作状态，但没有可直接读取的角度。

游戏行为包：

```text
Game/chr/c0000.behbnd.dcx
  bhv/basenormal.bhv
  bhv/weapon.bhv
  tae_action/b000.tae
```

已成功解包，但当前仓库内没有可用的 BHV 解析器，`basenormal.bhv` 也没有可直接搜索的动作名称字符串，所以还没有得到“机体鉴赏状态 -> 推进器动作”的映射。

另一个异常是：推进器 TAE 当前可见条目名类似 `a000_000000/a000_000100/...`，而 parts binder 内实际 HKX 文件是 `a000_000000` 到 `a000_000003`。这个 alias/mini-header 映射尚未解出，不能默认两边按文件名一一对应。

## 6. 需要咨询者明确解决的问题

请优先给出可由数据复现的答案，不要仅提供按截图手调后的欧拉角。

1. AC6 的机体鉴赏/机库状态究竟通过哪个 TAE、BHV 状态、HKX action ID 和采样时间设置推进器稳定姿态？
2. 推进器 TAE 中 `0/100/200/1000/2000/3000` 等条目与 binder 内 `a000_000000` 到 `a000_000003` 的真实映射是什么？请读取 mini-header/animation file ID，而不是按显示名猜测。
3. Havok pose 四元数进入 glTF，再由 glTFast 进入 Unity 时，装备根骨应该怎样做镜像共轭？当前 `Tools/DotNet/AC6PartToGltf/Program.cs` 中：

   ```csharp
   rotationDelta = tr.R * Inverse(sourceRest.R);
   q = rotationDelta * targetRest.R;
   ```

   对 System.Numerics 的行向量约定和 glTF 节点局部旋转来说，乘法顺序是否正确？
4. 原始 `+19.972° X` 在游戏坐标中对应喷管向下还是向上？请用 `_dmy_31` 的世界轴或喷铃几何轴做数值验证，不要只看根骨欧拉角。
5. 核心 `ref 800` 是完整产品安装 socket、焰点，还是只用于 SFX？如果只提供位置，决定安装旋转和嵌入深度的原生数据在哪里？
6. 是否还应组合核心上的其他 dummy、推进器上的 `_dmy_31/41/51` 或 `CBS_001_M_001` 万向节骨，才能恢复座面和轴向？
7. 不同核心的截图姿态差异是“核心 socket 变换”还是“同一推进器动作在不同核心上通过约束求解”的结果？请至少对 `BD_M_1010/2010/3010` 给出可比较矩阵。

## 7. 期望的修复方式

可接受的修复必须满足：

1. 仍然使用 AC6 原生 FLVER/HKX/TAE 管线，不恢复 Blender 烘焙模型。
2. 核心只决定自身接口；推进器仍是可自由替换的独立部件，不能把某个 `BS_A_*` 永久绑死到同编号核心。
3. 五套验证 loadout 中，每台都只有两个后方可见推进器实例，核心前方 `ref 800` 只作为必要的效果点处理。
4. `flameToSocket`、座面距离和最终喷管轴同时通过，不能只验证某个 dummy 重合。
5. 静止展示姿态与六张游戏原图一致：喷管稳定朝后下方，并随核心改变位置、角度和嵌入关系。
6. 播放飞行、QB、制动和矢量动作时不发生双重旋转、跳帧、脱座或累计漂移。
7. 修复要附带坐标链说明、action/TAE 映射表、数值报告和固定视角截图，保证下一台机器可以重跑。

## 8. 当前工作树说明

- 本轮临时的 `a000_000001` 展示姿态实验已撤销，不提交错误代码。
- `Assets/Scenes/SampleScene.unity` 是用户已有的未提交修改，不属于本咨询提交，也不得回退或暂存。
- 本次提交应只包含本日志和六张游戏参考截图。

## 9. 咨询结论（2026-07-14，开发机数值验证）

以下结论全部由仓库内已提交的数据数值复现得出，验证脚本方法附后。逐条回应第 6 节问题。

### 9.1 问题 4 的裁决：+19.972° X 在原生数据中就是"喷管向后下方"

直接解码 `Raw/NativeEquipment/BS_A_1010_NATIVE.glb`（未经 glTFast，与导出器输出逐位一致）：

```text
_dmy_31/41/51 世界 forward（glTF 右手 Y-up 空间）：
  rest（参考姿态）      = ( 0,  0.000, +1.000)   水平朝 +Z
  a000_000001 第 0 帧   = ( 0, -0.342, +0.940)   俯角 19.97°，朝后下方
```

与六张游戏原图的后下姿态一致。**HKX → 导出器 → GLB 这段链路是正确的，`a000_000001` 第 0 帧极可能就是机库展示姿态。**

### 9.2 问题 3 的裁决：乘法顺序正确，System.Numerics 是 Hamilton 约定

PowerShell 实测 `System.Numerics.Quaternion`：`Transform(v, qA*qB)` 等价于先施加 qB 再施加 qA，即 `operator*` 与数学 Hamilton 约定和 Unity 一致（并非 XNA 翻转约定）。因此：

```csharp
rotationDelta = tr.R * Inverse(sourceRest.R);   // = tr ⊗ rest⁻¹（父空间 delta）
q = rotationDelta * targetRest.R;               // = tr ⊗ rest⁻¹ ⊗ targetRest
```

是父空间 delta 重定向的标准公式，正确。9.1 的 GLB 实测反向印证了这一点。

glTFast 侧同样无罪：对比金标 socket（Unity 空间）与原始 GLB 坐标，`AM_M_1010 _dmy_25` 原始 x=+2.895 → 金标 −2.895，`BD_M_1010 _dmy_13` 原始 +1.458 → 金标 −1.458，Z 全部保持——glTFast 使用 X 镜像约定，四元数变换为 `(x,−y,−z,w)`，绕 X 的纯俯仰旋转完全不受影响。

### 9.3 真正的病灶：核心后方 `_dmy_800` 的节点朝向在 GLB 中被 180° 掉头（回应问题 5/6/7）

解码 `Raw/Gltf/BD_M_1010.glb` 的三个 `_dmy_800` 节点：

```text
前方  pos=( 0.000, 8.622, -2.225)  fwd=( 0, 0, +1)   朝后
后左  pos=(-0.413, 8.944, +2.402)  fwd=( 0, 0, -1)   朝前 ← 180° yaw
后右  pos=(+0.413, 8.944, +2.402)  fwd=( 0, 0, -1)   朝前 ← 180° yaw
```

这与本日志第 3 节 FLVER 诊断表（后方 Forward=+Z）**矛盾**——180° 是导出器的 dummy 朝向转换引入的（疑似按 glTF "−Z 为前"的 LookRotation 约定重建了 dummy 基向量；前方 dummy 的 FLVER 朝向本为 −Z，被同一变换翻成 +Z，恰好互为镜像证据）。

失败实验的每个数字由此完全解释：

```text
推进器 wrapper 为 identity → 直接继承 socket 的 180° yaw
GLB 第 0 帧 fwd ( 0, -0.342, +0.940)
  绕 Y 转 180°  → ( 0, -0.342, -0.940)
  验证器取 −flame.forward → ( 0, +0.342, +0.940)   ← 与实验打印值逐位一致
backDot = +0.940 ✓、pitchDeg = +19.97°（向上）✓
```

验证器 `exhaustDir = (−flame.forward)`（AC6SkinnedModularSampleBuilder.cs:464）的负号在 idle 水平姿态下与 180° yaw 互相掩护，两错相抵通过了基线验证；动画一采样即暴露。

对问题 7 的直接回答：三个核心的后方 socket 朝向完全相同（都是同一个被翻转的 180° 帧），**俯仰角不来自核心 socket，而来自推进器自身动作**；核心差异体现在 socket 位置（第 3 节表格）与嵌入偏移。若游戏内实测各核心俯仰角确有差异，才需要继续找第二数据层（见 9.5 遗留项）。

### 9.4 修复方案（满足第 7 节全部要求）

1. **修导出器**：`AC6PartToGltf/Program.cs` 的 dummy 朝向转换改为直接以 FLVER Forward/Up 构建节点旋转（Forward→+Z 基），移除 −Z-forward 约定；重新生成全部 GLB（核心、推进器、武器）。
2. **修验证器**：`exhaustDir = +flame.forward`（负号随 180° 一起退场）；其余断言不变。修复后期望值：idle 水平；`a000_000001` 第 0 帧 pitch = −19.97°（后下），与截图一致。
3. **恢复被撤销的实验**：socket 修复后，重新启用"装配时采样 `a000_000001` 第 0 帧作为展示姿态"，此时符号正确。
4. **警惕连带翻转**：武器挂载可能一直依赖"socket 与武器 dummy 双双翻转、两错相抵"才显示正确（`_dmy_200/_dmy_13/_dmy_14` 同样过了这个转换）。重新生成 GLB 后必须整套重跑金标验证，武器朝向若翻转说明其原本就依赖病灶，需同步修正挂载约定。

### 9.5 游戏机上的遗留验证项

- 用 FLVER 直读确认后方 `_dmy_800` 原始 Forward 确为 +Z（钉死导出器转换这一环）。
- 问题 1/2（机库状态的 BHV/TAE 路由、TAE 条目与 binder 内 HKX 的 mini-header 映射）仍未解，但已不阻塞展示姿态闭环：`a000_000001` 第 0 帧 + 修复后的 socket 帧即可数据复现截图姿态。
- 游戏内逐核心量一次实际俯仰角：若三核心确实不同，按 9.3 结论需另找数据层；若相同（约 20°），本方案即闭环。

### 9.6 复现方法

- GLB 解码：Python 直接解析 GLB JSON/BIN chunk，按 Hamilton 四元数组合节点层级，输出 dummy 世界 forward（rest 与动画第 0 帧覆盖对比）。
- System.Numerics 顺序：`Transform(v, qA*qB)` 与顺序施加对照（qA=90°X，qB=90°Y，v=+Z）。
- glTFast 约定：仓库金标 socket 断言值（builder 600-649 行）对照原始 GLB 世界坐标。

## 10. 游戏机实施回报（2026-07-14）

> 本节记录了当时的中间结论，其中 `identity` 安装和 `-flame.forward` 已被后续可见模型检查证伪。最终结论见第 11 节。

已按第 9.4 节修正导出器并重建 12 个框架 GLB、3 个推进器 GLB 和 8 个武器 GLB。Unity 自动验证随后暴露出咨询结论遗漏的一条原始语义：

```text
BS_A_1010.flver --dump-dummies
ref 31 Forward=(0,0,-1.95) Up=(0,0.54,0)
ref 41 Forward=(0,0,-1.95) Up=(0,0.54,0)
ref 51 Forward=(0,0,-1.95) Up=(0,0.54,0)
```

即核心后方 `ref 800 Forward=+Z`,但推进器焰点 `ref 31/41/51 Forward=-Z`。导出器仍应把 FLVER `Forward` 正确映射到 glTF 局部 `+Z`;然而推进器 dummy 的 Forward 指向喷管内部,物理排气方向应为 `-flame.forward`,不能使用第 9.4 节建议的 `+flame.forward`。这不是恢复旧的 socket 翻转,而是两种原生 dummy 各自的语义不同。

最终 Unity 报告：

```text
5/5 loadout PASS
每台可见后推进器数量 = 2
flameToSocket = 0m
exhaustDir = (0,-0.342,+0.940)
backDot = 0.940
pitchDeg = -19.97°
34 个接口骨 position/rotation/scale error = 0
左右手武器 gripDistance = 0.0747m .. 0.1482m
Validation passed for 5 loadouts.
```

对应报告为 `Assets/_Project/LocalSamples/AC6SkinnedModular/Reports/skinned_modular_validation.txt`。框架、武器和推进器 GLB 均来自原生 FLVER/HKX/DDS 管线,未恢复 Blender 烘焙资源。

## 11. 可见模型反装回归与最终修正（2026-07-14）

用户对 Unity 近景图进行人工检查后确认：第 10 节虽然数值验证通过，但推进器可见模型仍然前后反装，喷嘴出口朝向核心。根因是两个错误互相抵消：

1. 推进器 wrapper 保持 identity，完整可见模型相对核心后部 socket 反装；
2. 验证器使用 `exhaustDir = -flame.forward`，把反装后的 dummy 方向再次取反，因此仍得到后下方的“正确”数值。

最终安装与验证契约改为：

```text
booster resource root -> selected core rear ref 800
local position        = (0, 0, 0)
local rotation        = Euler(180, 0, 0)
local scale           = (1, 1, 1)
exhaustDir            = +flame.forward
```

重新生成 1010/2010/3010 与 Mixed A/B 后，五套 loadout 全部通过。报告值仍为 `exhaustDir=(0,-0.342,+0.940)`、`backDot=0.940`、`pitchDeg=-19.97°`，但现在它来自实际正向而非负号补偿。`Closeup_Uniform1010_Boosters_side_t000.png` 与 `Closeup_Uniform3010_Boosters_side_t000.png` 也确认喷嘴出口远离核心并朝机体后下方。
