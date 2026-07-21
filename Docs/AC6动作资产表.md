# AC6 动作资产表

本表由 `Tools/BuildAC6ActionCatalog.ps1` 自动生成。人工结论只写入 `Tools/AssetManifests/c0000_action_labels.json`，不要直接编辑自动生成 JSON。

## 总览

| 指标 | 数量 |
|---|---:|
| TAE 逻辑动作名 | 1295 |
| 有物理 HKX 的动作 | 678 |
| 仅 TAE 引用、缺物理 HKX | 617 |
| TAE 状态记录 | 1867 |
| 已人工确认 | 0 |
| BHV 官方状态映射 | 106 |
| 待人工确认 | 1189 |
| 已生成 pose JSON | 636 |
| 已生成四部件 Unity Clip | 633 |
| 可进入运行时正式清单 | 40 |

## 使用规则

1. `OfficialMapped` 表示动作 ID 已通过 BHV 状态索引和逐状态转换数量双重校验，官方状态名可直接采用。
2. `NeedsReview` 表示仍只有宽泛分类；无法被 BHV 主动作槽解释的动作继续保留人工目检流程。
3. 人工检查只填写人工备注。只有明确人工裁决时才使用 `Confirmed` 或 `Rejected` 覆盖官方/程序结论。
4. `OfficialMapped/Confirmed + PoseReady + 四部件 Clip` 可进入运行时正式清单。
5. 完整逐项数据见 `Tools/AssetManifests/c0000_action_catalog.json` 和 UTF-8 CSV。

## 分类统计

| 分类 | 数量 |
|---|---:|
| 武器姿态与攻击动作 | 609 |
| 冲击或受击反应候选 | 277 |
| 机体基础与移动动作 | 151 |
| 机体官方行为状态 | 106 |
| 倒地或恢复候选 | 50 |
| 机体移动或姿态候选 | 39 |
| 地面移动或接触候选 | 34 |
| 特殊演出动作 | 14 |
| 机体特殊动作 | 13 |
| 系统或调试动作 | 2 |
