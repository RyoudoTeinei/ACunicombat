# AC6 动作库管线设计

日期：2026-07-17

目标：把"新增一个机体动作"收敛为**改一行清单 + 跑两步**，不再为每个动作手工走一遍 HKX 提取、姿态导出、Clip 适配、资产挂接。

## 1. 管线全貌

```text
①  数据判定（人工，一次性）
    Docs/AC6机体动作判定表_2026-07-17.md  →  选定 actionId 与语义

②  清单登记（人工，每动作一行）
    Tools/AssetManifests/c0000_action_manifest.json
    字段：actionId / hkx / layer(FullBody|UpperBody) / loop / blendIn/Out / enabled / note

③  步骤 A：HKX → 全采样 pose JSON（脚本，幂等）
    powershell -File Tools/BuildAC6ActionClips.ps1 [-Force]
    · 按清单在 Tools/LocalOut/anibnd/ 递归找 *_<hkx>.hkx 与同目录 compendium
    · AC6HkxPoseExporter → Tools/LocalOut/actions/<actionId>_pose.json
    · 已存在则跳过（-Force 覆盖）；HKX 未解包时告警并计数，不中断

④  步骤 B：pose JSON → 每部件 Clip + 注册表（Unity 菜单，幂等）
    Tools > ACunicombat > AC6 > Build Action Library（AC6ActionLibraryBuilder.cs）
    · 对 HD/BD/AM/LG 四个框架 GLB 各生成一条 Clip：
      Generated/Actions/<actionId>/AC6_<actionId>_<TOKEN>.anim
    · 更新（upsert）单一注册表资产：
      Generated/Actions/AC6ActionRegistry.asset（MechActionRegistry）
    · Clip 资产按 GUID 稳定更新（CopySerialized），场景/预制引用不失效

⑤  消费（运行时，已就位）
    MechBodyActionPlayer.Configure(registry, runtimeState, upperBodyBones)
    PlayFullBody / PlayUpperBody(actionId)
```

清单是唯一数据入口；③④ 全部幂等可重跑。

## 2. 关键设计决策

### 2.1 重定向数学（步骤 B 核心）

pose JSON 携带 canonical（c0000 HKX 参考骨架）的逐帧 local TRS 与参考姿态。写入部件 Clip 时按装备管线同族公式换基：

```text
q_part(f) = q_anim(f) ⊗ q_restCanonical⁻¹ ⊗ q_restPart
t_part(f) = t_anim(f) − t_restCanonical + t_restPart
```

已验证的常态是**部件 bind pose == canonical rest**（FLVER vs HKX rest 对比结论），此时 delta 为恒等、曲线原样通过。构建器对每根骨检测 delta：出现非恒等 delta 会在日志给出计数警告——那意味着重定向路径真的被走到了，该部件必须目检后再采信。

### 2.2 只为部件拥有的骨写曲线

canonical 130 骨中，每部件只映射自己层级里存在的骨（按名匹配）。四个部件消费同一份源动作，接口骨（34 根）在各部件中获得相同曲线，残余漂移由运行时 `MechSynchronizedAnimationPlayer`/`MechBodyActionPlayer` 的共享骨同步兜底——与现行架构一致，不新增机制。

### 2.3 Clip 资产与注册表的稳定性

- Clip 命名 `AC6_<actionId>_<TOKEN>.anim`，目录按 actionId 分组；重建时用 `EditorUtility.CopySerialized` 覆写现有资产，GUID 不变。
- 注册表是**单一资产** `AC6ActionRegistry.asset`，构建器按 actionId upsert（新增或更新条目），手工在 Inspector 里补充的字段（如后续加的效果时间线引用）不会被整表重建冲掉——前提是新字段的默认值语义为"不覆盖"。
- 换装不影响注册表：Clip 针对骨架层级，同槽位不同部件只要骨名一致即可复用（AC6 框架部件正是如此）；若未来出现异构骨架部件，注册表的 slotClips 支持按 slot 提供专用 Clip。

### 2.4 向 canonical rig（方案 3）的迁移路径

现在的"每动作 × 4 Clip"是方案 2 的形态。共享骨架统一后：

- 步骤 B 改为输出单条 canonical Clip（去掉换基与四次循环，代码删多于改）；
- `MechBodyActionDefinition.slotClips` 留一条 `slot=None` 的条目即可，`ResolveClip` 已按此设计回退；
- 清单、注册表结构、运行时 API 全部不变。

### 2.5 上半身动作（a2xx bank）的接入方式

a2xx 武器手臂动作与 a000 机体动作走同一条管线：清单里 `layer=UpperBody`，HKX 换成对应 bank 的文件（需先把 `c0000_a2xx_yy.anibnd.dcx` 解包进 `Tools/LocalOut/anibnd/`）。运行时由 `MechBodyActionPlayer` Layer 1 + AvatarMask 播放。清单中已放入 a200 的抬臂/保持/开火三条**禁用**样例，武器类别→bank 映射确认后置 `enabled: true` 即可。

## 3. 现状与待执行清单

框架已就位（本提交）：清单（含 5 条启用的 a000 动作 + 3 条禁用的 a200 样例）、ps1 驱动、编辑器构建器、注册表 upsert、运行时播放器。**尚未执行/验证的**：

1. 跑一次 ③④，目检 5 条动作在四部件上的效果（重点看非恒等 delta 警告是否为零）；
2. showcase 接线：给机体挂 `MechBodyActionPlayer`、`Configure(registry, runtimeState, 上半身骨名列表)`、controller 三个动作 ID 字段填入（`a000_010102` / QB 序列 / 上半身暂空）；
3. 上半身骨名列表的确定（Spine1 以上 + 双臂链，从骨架 JSON 按名筛选）；
4. 判定表第 4 节的三个目检项与武器 bank 映射。

## 4. 已知限制（有意为之）

- 步骤 A 依赖本机已解包的 anibnd 与已构建的 exporter，脚本只告警不自动解包/编译——保持每步职责单一、失败显式。
- 曲线未做关键帧压缩（全采样逐帧 key）。130 骨 × 300 帧的 Clip 约 1-2 MB，首版可接受；需要时在 WriteBoneCurves 后加误差阈值抽稀，属局部优化不动结构。
- pose JSON（约 3 MB/条）留在 `Tools/LocalOut/actions/`，是否入库由仓库现行 LocalOut 策略决定，管线不做假设。
