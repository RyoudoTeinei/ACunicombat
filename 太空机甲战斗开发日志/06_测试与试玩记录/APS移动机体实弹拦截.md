# APS 移动机体实弹拦截

日期：2026-10-15。

## 复现范围

新增真实飞行控制测试，通过 MechFlightMotionController.Tick 接收与玩家相同的 MoveLocal 输入，经喷口、推力、质量积分移动机体，不直接写入速度或每帧修改位置。测试机为简化六轴喷口载体，未加载完整玩家模型与 MechPlayerRig 的动画、相机等系统。

在 30/60 FPS 下持续前推、横移（约 150 m/s）、横向加速（约 200–280 m/s）。敌方拥有独立雷达上下文，导弹启用真实导引头、初始观测与转弯控制。真值只用于离线记录与伤害判定，未注入 APS 航迹。

## 已证实的问题与改动

- 近防雷达已有 LOS 率观测，但共享 RadarTrackManager 只用位置与径向速度。横向机动速度收敛滞后。现在对经过所属观察者验证的 CloseProtectionReceiver 观测融合两个切向速度分量，补偿本机运动，并使用测量方差赋权；未放宽源归属检查。
- APS 先前仅对位置外推，求解仍冻结原始速度。现在使用同期速度与带加速度/jerk 的预测轨迹；弯曲轨迹不再被匀速方程的无解或匀速 CPA 时间提前否决。
- Update/LateUpdate 发射时刻与固定物理步之间存在时间差。求解外推到实际发射时刻，拦截弹第一物理步只积分出生后的时间。

## 修复前后证据

`APS-Moving-20261015-enemy-before` 的横向加速用例：实际发射并命中，但导弹距爆点 5.16 m，只损伤未摧毁。

`APS-Moving-20261015-regression`：六项移动制导拦截均已产生 hits=1、kills=1，爆点到导弹距离约 2.68–3.17 m。保持 900 m/s 初速、35 m 最小引爆距离、8 m 伤害半径、160 基础伤害与原伤害衰减。

本轮准确复现的是移动中的拦截失败；用户实战中“完全不触发”的具体状态尚无现场轨迹记录，不能把所有情况都归为同一已验证根因。直线来袭的静止或移动用例通过不足以替代制导机动用例。

## 验证状态

隔离 URP 相关回归：61/61 通过，包含六项 30/60 FPS 移动制导实弹、共享航迹、APS 与音效回归。结果见 `Artifacts/SensorBench/APS-Moving-20261015-regression/test-results.xml`。

主项目完整 Baseline：被 Unity 项目占用检查阻止，已请用户保存并关闭编辑器，尚未运行。不得将隔离工程测试描述为主项目完整验收。


## 实战日志复核补充

用户指出应先查看实战记录后，已核实 `SimulationDriver.enableCombatTelemetry` 默认开启，编辑器实战日志自动写入 `Logs/CombatTelemetry/`。之前未先检查这些日志就构造复现测试，排查顺序有误。

已读取 `combat_20261015_015232_870_AC6PlayerControl.jsonl`（约 28 秒的场景运行）和 `combat_20261015_014733_886_AC6PlayerControl.jsonl`（约 119 秒）。文件包含双方航迹、导弹融合、来袭判定、发射与自毁事件；未找到 APS 专属决策事件，APS 的拒绝原因、准备进度和弹药状态仍主要保留在内存诊断中。

最近一份日志 simTime 9.96 秒，玩家观测到 trackId=16777217 为 WeaponsQuality、incoming=true、positionUncertaintyMeters≈3.03、timeToCpaSeconds≈0.088。结合敌方对玩家的邻近时间航迹估算，距离约 162 m、玩家速度约 93 m/s。后两项是交叉估计，不是玩家真值，也不能据此认定具体的 APS 拒绝分支。

因此“没有实战日志”的说法不准确；准确缺口是：现有实战遥测没有与 APS 决策状态形成完整关联。已有实战日志应优先用于约束复现场景，再补缺失事件，而不是要求用户凭记忆描述 HUD。


## 主项目回归完成

用户关闭 Unity 后已完成 Baseline：438/438 测试通过，包含六项 30/60 FPS 移动制导拦截。输出 `Artifacts/SensorBench/20261015-033746-e4efaafe/`，测试源码指纹与当前文件一致。contractGate Passed；性能阈值未评审，capabilityGate Inconclusive。此结果不补齐旧实战日志缺失的 APS 决策信息。
