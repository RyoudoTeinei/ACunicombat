# AC6 动作资产表与导入管线

日期：2026-07-17

目标：为 AC6 机体和武器动作建立可追踪的自有资产表。程序负责穷举、宽泛分类和生成预览资源；具体语义必须经过人工目检，未确认动作不得进入正式动作库。

## 1. 数据范围

当前扫描结果：

| 项目 | 数量 |
|---|---:|
| 逻辑动作 ID | 1229 |
| 有实体动画 HKX | 632 |
| 仅有 TAE 引用、缺少同名 HKX | 597 |
| TAE 行 | 1867 |
| 已确认动作 | 0 |
| 待人工确认动作 | 1229 |

逻辑动作 ID 是 TAE 引用与实体 HKX 文件名的并集。`Skeleton.hkx` 和 `c0000.hkx` 是支持文件，不计入动作数量。

## 2. 资产表文件

### 2.1 人工维护源

`Tools/AssetManifests/c0000_action_labels.json`

这是唯一需要人工维护的文件。每个覆盖项以 `actionId` 为键，记录：

- `displayNameZh`：确认后的中文动作名称；
- `categoryKey` / `categoryZh`：程序分类键与中文分类；
- `descriptionZh`：中文动作说明；
- `reviewStatus`：`NeedsReview`、`Confirmed` 或 `Rejected`；
- `confidence`：`Low`、`Medium` 或 `High`；
- `layer`：`FullBody` 或 `UpperBody`；
- `loop`：是否循环；
- `manualNoteZh`：人工目检依据和备注。

### 2.2 自动生成文件

- `Tools/AssetManifests/c0000_action_catalog.json`：完整机器可读总表；
- `Tools/AssetManifests/c0000_action_catalog.csv`：可在表格软件中查看的中文总表；
- `Docs/AC6动作资产表.md`：数量、状态和分类汇总；
- `Docs/AC6动作待人工确认.md`：全部未确认动作的逐项清单。

不要直接编辑上述自动生成文件。修改 `c0000_action_labels.json` 后重新生成即可。

旧的 `c0000_action_manifest.json` 只保留作历史兼容和问题追踪，不再是正式动作库的数据源。

## 3. 分类原则

程序按 bank、动作编号范围和 TAE 事件做保守的宽泛分类，包括：

- 机体基础与移动动作；
- 机体移动或姿态候选；
- 地面移动或接触候选；
- 冲击或受击反应候选；
- 倒地或恢复候选；
- 武器姿态与攻击动作；
- 机体特殊动作；
- 特殊演出动作；
- 系统或调试动作。

分类只用于缩小搜索范围，不等于确认具体语义。程序不能仅凭编号可靠断言某条动画是 QB、AB、飞行、抬臂、开火、受击或倒地。

## 4. 标准工作流

### 4.1 重建完整资产表

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/BuildAC6ActionCatalog.ps1
```

脚本读取：

- `Tools/LocalOut/c0000_tae_dump.json`；
- `E:\UnityDev\Resources\AC6_ExtractedAssets\chr\AC - c0000` 下的实体 HKX；
- `Tools/AssetManifests/c0000_action_labels.json` 中的人工覆盖。

输出总表、CSV、汇总文档和待确认文档。重复运行是幂等的。

### 4.2 在 Unity 中人工确认

打开：

`Tools > ACunicombat > AC6 > Action Catalog`

窗口提供：

1. 中文名称、分类、来源、状态搜索和筛选；
2. 实体 HKX、Pose JSON、Unity Clip 就绪状态；
3. 单动作按需解码；
4. 在当前装配机体上按时间轴预览；
5. 保存中文名称、中文说明、层级、循环属性和人工备注；
6. 确认或排除动作。

窗口不允许把仍以“待人工确认”开头的暂名直接标成 `Confirmed`。

### 4.3 按需生成预览动作

在窗口选择有实体 HKX 的动作，点击“生成 Pose + 四部件 Clip”。命令行等价形式：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/BuildAC6ActionClips.ps1 -ActionId a000_000000 -Force
```

解码链路：

```text
实体 HKX + c0000 Skeleton + 真实 compendium
    -> AC6HkxPoseExporter (.NET 9)
    -> Tools/LocalOut/actions/<actionId>_pose.json
    -> AC6ActionLibraryBuilder.BuildReviewAction
    -> HD / BD / AM / LG 四个 Unity AnimationClip
```

全量解码 632 个实体动作可能产生数 GB 中间文件，因此默认只解码已确认动作或指定的 `-ActionId`。确实需要全量时显式使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/BuildAC6ActionClips.ps1 -AllPhysical
```

### 4.4 构建正式动作库

人工确认完成后运行：

`Tools > ACunicombat > AC6 > Build Action Library`

正式构建器只读取 `c0000_action_catalog.json` 中 `reviewStatus == Confirmed` 且有 Pose 的条目。`NeedsReview` 和 `Rejected` 都不会进入 `AC6ActionRegistry.asset`，展示场景和运行时不能再把历史猜测当作 QB、AB、飞行或开火动作。

## 5. 动画重定向与换装

同一动作会为 HD、BD、AM、LG 四个框架部件分别生成 Clip。构建时只给该部件实际拥有的骨写曲线，接口骨继续由现有共享骨同步机制约束，因此动作可以用于自由换装机体。

参考姿态换基公式：

```text
q_part(f) = q_anim(f) * inverse(q_restCanonical) * q_restPart
t_part(f) = t_anim(f) - t_restCanonical + t_restPart
```

Clip 路径固定为：

```text
Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/<actionId>/
    AC6_<actionId>_HD.anim
    AC6_<actionId>_BD.anim
    AC6_<actionId>_AM.anim
    AC6_<actionId>_LG.anim
```

重建现有 Clip 时使用稳定 GUID 覆写，场景和预制引用不会因重建失效。

## 6. 当前验证结果

- 完整总表：1229 条，动作 ID 无重复；
- 中文标记完整性：1229/1229 均有中文暂名、中文分类、中文说明和复核状态；
- 单动作解码：`a000_000000` 成功导出 301 帧、130 骨；
- Unity Clip：成功生成 HD/BD/AM/LG 四段预览 Clip；
- Unity 6.5 隔离批处理编译：退出码 0，无 C# 错误；
- 正式库门禁：0 条 `Confirmed` 时构建结果为 `built=0, failed=0`，注册表条目数为 0；
- 正式动作库当前确认数：0，避免继续使用此前已经证实错误的语义猜测。

## 7. 人工确认要求

人工确认时至少记录：

1. 动作开始、中段和结束姿态；
2. 是否循环、是否包含位移；
3. 是全身动作还是上半身动作；
4. 是否需要武器、推进器或特效配合才能判断；
5. 判断依据和仍存在的疑点。

无法确认时保持 `NeedsReview`，在 `manualNoteZh` 中写明观察结果。不要为了让演示先动起来而猜测具体语义。
