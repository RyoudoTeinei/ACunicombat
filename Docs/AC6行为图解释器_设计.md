# AC6 行为图解释器设计：任意动作自由衔接的通用方案

日期：2026-07-19

## 1. 核心立场

"原版那样任意动作自由衔接"不是一个要实现的功能，而是一张已经拿到手的图：`basenormal.bhv` 的 **222 状态、8673 条转换**（双重验证零漂移）。特事特办一组组接动作之所以感觉没用，是因为把图的**内容**当成了要手写的东西。

**正确做法：内容零手写，只写一个通用解释器。** demo、玩家、AI 跑的是同一台机器，差别只在谁来填输入快照——demo 从"脚本化动画"变成"脚本化输入"，特事特办从结构上消失。

## 2. 可行性数据（决定性）

- 8673 条转换共 33082 次条件引用，**去重后只有 140 个条件原子、40 个家族**；
- 条件语法规整：`家族 谓词 [反転/一致]`；
- 覆盖率曲线（映射前 N 个高频原子 → 完整可用转换比例）：

| N | 10 | 20 | 30 | 50 | 80 | 110 | 140 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 可用转换 | 2% | 20% | 42% | 74% | **96%** | 99% | 100% |

映射 80 个原子（每个≈一行绑定）换整张图 96% 永久可用，有限工程量。

- **取反默认值红利**：未映射原子恒假，其"反転"变体因取反恒真。最高频的四个原子——エネルギー空っぽ反転(3424)、ソフトアセン反転(1437)、プロペラ判定反転(1424)、オーバーヒート反転(824)——全是反転形，**零映射即获得正确默认行为**。这是首批自动分类只映射 56 原子就覆盖 67% 引用的原因。

## 3. 架构五件套（本次交付的代码）

```text
数据流：
basenormal_states.json (222状态+转换+条件文本, ParseBhvStateTable.py 产出)
c0000_condition_catalog.json (140 原子→求值绑定, 生成+人工增补)
c0000_blend_map.json (1171 条 TAE 官方 blend-in, 生成)
c0000_state_anim_overrides.json (QB 等别名/占位覆盖, 人工)
        │  AC6BehaviorGraphCompiler (Editor 菜单: Compile Behavior Graph)
        ▼
MechBehaviorGraphAsset (ScriptableObject, 扁平数组)
        │  MechBehaviorGraphRunner.Tick(ctx, dt)
        ▼
MechBodyActionPlayer.PlayFullBody(actionId)   ← 现有播放器，不改
```

1. **`MechBehaviorGraphAsset`**（运行时数据）：states/transitions/conditions/atoms 四个扁平数组 + 索引区间。转换按存储序排列 = 原版裁决序。
2. **条件目录**：140 原子 → `MechConditionKind` + param。已自动映射 56 个（PadInput 28 位、着地/能量/锁定/攻击中/ガード/动画结束/キャラ制御フラグ 5 位/状态类型 5 种），其余 ConstFalse 待增补。padBits/ccfBits/typeIds 的位定义就在目录 JSON 里。
3. **`MechBehaviorGraphRunner`**（通用解释器，唯一的运行时组件）：每 Tick 全原子求值成**栈上 4×ulong 位掩码**，按序扫当前状态转换，首个全真且目标可用者跳转，播动作用 TAE 官方 blend-in。
4. **`MechGraphContext`**（唯一消费方边界）：blittable 输入快照——PadBits、CharControlFlags、接地/能量/锁定等布尔、状态类型枚举。玩家控制器/AI/demo 脚本只填它。
5. **优雅降级**：未映射原子→转换沉默；无动画状态→不可进入，候选顺延。图第一天就能跑，覆盖率随目录映射与动作确认单调上升——与动作目录的人工确认工作流天然对齐：确认一个动画，图上对应状态自动激活。

## 4. 引擎级优化清单（已实现）

- 运行时零字典、零字符串比较、零 GC：字符串只存在于 `Atom.debugText`（调试）与编译期；
- 条件求值 = 位掩码测试：140 原子一次求值进 `stackalloc ulong[4]`，单转换判定为 O(条件数) 次位运算；
- 扁平数组 + `ref readonly` 遍历：状态/转换/条件连续内存，缓存友好；
- 单 Tick 链式跳转封顶（默认 4）防瞬时条件状态环死锁一帧；
- Tick 由外部驱动，与运动模拟同时基（可挂 FixedUpdate 或 SimulationDriver）；
- 转换历史环形缓冲（64 条）供验证断言，无分配。

## 5. 条件家族 → 绑定的映射规则

| 家族（原子数） | 绑定 | 数据源 |
| --- | --- | --- |
| パッド入力 (28) | PadInput, param=位序 | 输入快照 PadBits（位名在目录 padBitNames） |
| 着地判定 (8) | Grounded/Airborne | 运动模拟接地标志 |
| エネルギー (2) | EnergyEmpty | 能量系统 |
| ターゲットロック中/攻撃中 (7) | LockedOn/TargetAttacking | 锁定系统 |
| アニメーション終了 (1) | BodyAnimFinished | 解释器内部（Runner 覆盖求值） |
| キャラ制御フラグ (5) | CharControlFlag, param=位序 | **当前动画的 TAE ChrActionFlag 窗口**——TAE 事件→运行时旗标→BHV 条件，就是原版可打断窗口的官方闭环 |
| 攻撃/本体ステート行動タイプ (8) | XStateType, param=typeId | 上层模式状态（ブースト/エイム…） |
| 特殊効果系/速度/ダメージ等 (84) | ConstFalse 待增补 | 反転形自动正确；正形待接对应系统 |

## 6. 分阶段

- **P1（本次已交付）**：解释器 + 编译器 + 56 原子映射。最小闭环（Idle→AB→巡航→QB→刹车）应从图里涌现——demo 脚本只按时间序列填 PadBits；
- **P2**：把 ConstFalse 原子按 §5 规则接真系统（速度/伤害/特殊效果），到 80+ 原子（96%）；ステート経過時間类需要 BHV 条件参数二进制的阈值，届时再逆向该段；
- **P3**：同一解释器实例化第二份跑 `w0xx` 武器图（我方解析器对 w 图同格式适用），作上半身层；
- **P4**：State 内写入 clip 时长（替换 Runner 的 animFinished 近似）、辅助动画槽（附加倾斜层）、条件参数二进制。

## 7. 验证方式（升级）

不再问"这组序列对不对"，改问：**相同输入序列下，Runner 的状态 ID 轨迹是否符合图**。用 `StateChanged` 事件 + `CopyHistory` 断言：

```text
给定 PadBits 时序（起跳→长按上升→推进→QB→松开）
断言状态轨迹 ⊆ {0 待機, 28/29 ジャンプ, 47 ブースト飛行, 82 空中QB, 68-70 ブレーキ, ...}
且每步转换存在于图中（Runner 结构上保证）
```

满足咨询日志验收标准 8 的"检查当前状态 ID 与转换进度，不检查日志字符串"。

## 8. 文件清单

```text
运行时  Assets/_Project/Scripts/Mech/Runtime/MechBehaviorGraph.cs        (资产+求值)
        Assets/_Project/Scripts/Mech/Runtime/MechBehaviorGraphRunner.cs  (解释器)
编辑器  Assets/_Project/Editor/AC6BehaviorGraphCompiler.cs               (菜单: Compile Behavior Graph)
数据    Tools/AssetManifests/c0000_condition_catalog.json                (140 原子, 可增补)
        Tools/AssetManifests/c0000_blend_map.json                        (1171 条官方 blend)
        Tools/AssetManifests/c0000_state_anim_overrides.json             (QB 别名/占位)
输出    Generated/AC6BehaviorGraph.asset                                 (编译产物)
```
