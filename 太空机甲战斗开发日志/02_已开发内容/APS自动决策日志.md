# APS 自动决策日志

日期：2026-10-15。

接入现有 CombatTelemetrySession，每次 Play 自动在 Logs/CombatTelemetry 创建 JSONL 日志。默认遥测已开启，沿用文件轮转、定期刷新及会话结束收尾机制，无需手动导出。

## 事件与字段

- target_selected、plan_accepted、plan_rejected：目标选择、准备或释放阶段的规划结果、具体原因、预测飞行时间和爆点（已形成解时）。
- preparation_started、state_snapshot：准备状态、剩余反应时间、准备比例、弹药、冷却、在飞数量、本机位置速度加速度、推进输入、观测航迹及精度。
- gate_rejected、action_cancelled：资源或有效性拒绝、航迹丢失、超时等取消原因。
- countermunition_launched、airburst：独立 projectileId 连接发射与空爆，包含预测/实际爆点、命中和摧毁数量，区分 Intercepted、DamagedOnly、Missed。
- burst_inhibited、countermunition_removed：近身安全禁爆或爆炸前移除。
- component_disabled、equipment_removed：组件关闭或卸装。

source、observerScope、equipmentId、slotId、actionId、targetTrackId 用于跨事件关联。目标字段为观测估计，物理命中结果只在事后记录。stateSampleTime 区分决策快照与空爆时刻；结果记录携带发射时设备快照，并更新实际爆点及本机状态。

准备中状态约每 0.05 秒采样，普通状态约每 0.25 秒采样；状态变化、发射与结果单独记录。规划按候选航迹和阶段限频，避免重复拒绝刷屏。退出 Play 后文件自动收尾。

## 验证

主项目 Defense 回归 109/109 通过：Artifacts/SensorBench/20261015-051816-729a6fff/。之后仅调整日志采样与关联，最终日志写入和移动实弹专项 5/5 通过：Artifacts/SensorBench/APS-Telemetry-20261015-final/。

已直接解析最终运行的 JSONL，检查中间准备进度、弹药从 8 到 7、拒绝事件，以及同一 projectileId/actionId 的发射、空爆与 kills=1。审计结果保存在 telemetry-audit.json。

用户后续两局应按可玩场景名、session_start 时间筛选，避免将自动测试的空场景日志当作实战；不要求用户回忆 HUD 或手动导出。
