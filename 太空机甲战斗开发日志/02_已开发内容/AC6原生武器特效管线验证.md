# AC6 原生武器特效管线验证

## 完成内容

- 建立从 AC6 公共 FFX Binder 提取指定武器 FXR 的自动脚本。
- 支持 FXR Proxy 递归解析。
- 支持提取 FXR 直接引用的 TPF 贴图。
- 支持导出 FXR 引用的 FLVER 特效模型为 GLB。
- 支持追踪模型材质引用的共享 TPF，并二次导出带原始贴图的 GLB。
- Unity 工厂新增 Tracer、Model、RichModel、Beam 等重建路径。
- 测试武器系统优先使用原生发射、弹体和命中特效，缺失时才回退到占位效果。
- 修复 `NoLight_Add` 特效模型被普通 PBR 材质渲染成巨大灰色面片的问题。
- 修复 FXR 贴图图集被整张显示成火焰墙的问题；Billboard 与加法模型现在读取图集列数、总帧数和帧范围。
- 修复所有 Billboard 被硬编码为一次发射 5 个粒子的问题；现在使用 FXR 原始 `perEmission`。

## 本轮样本

- MA-J-200 RANSETSU-RF
- DF-GR-07 GOU-CHEN
- BML-G1/P07VTC-12
- BML-G2/P03MLT-06

## 构建结果

- 请求 FXR：10 个。
- 递归解析后 FXR：13 个。
- Unity 发射器描述：51 个。
- FXR 直接贴图：9 张。
- 原始特效模型：8 个。
- 模型共享贴图：3 张。

8 个 GLB 均已包含贴图图像；其中 `s81032`、`s81035`、`s81040` 依赖的共享贴图分别为 `s34010_a`、`s33001_a`、`s31460_a`。

`s81032`、`s81035`、`s81040`、`s88060` 的 MTD 使用 `NoLight_Add`。这些模型是发光贴图的承载几何，不是实体弹体；其贴图黑底本身可能保持不透明，必须依靠加法混合消除黑底。Unity 工厂现只对这类模型替换为透明加法材质，`s88120`、`s88200`、`s88270`、`s88500` 等实体 PBR 模型保持原材质。

## 编译验证

- `Assembly-CSharp-Editor.csproj` 编译通过。
- C# 错误：0。
- 现有警告：114，主要为项目原有 Unity API 过时提示与序列化字段提示，本轮未新增阻断错误。

## 已知限制

- 当前不是 AC6 原生 FXR 渲染器，Unity 中的 Shader 与复杂节点仍为近似解释。
- RANSETSU-RF 已有命中 FXR 映射；GOU-CHEN 与两类导弹的准确命中/爆炸 FXR 尚未确认。
- 尚未对所有效果完成游戏内尺寸、朝向、透明混合和生命周期的逐帧视觉校准。
- 当前映射只覆盖可玩测试场景使用的四件武器，不代表已经完成全武器资产表。

## 验收方法

1. 运行 `Tools/BuildAC6NativeEffects.ps1`。
2. 打开 Unity，等待资源刷新完成。
3. 执行 `Tools > ACunicombat > AC6 > Build Playable Player Scene`。
4. 进入 Play，分别使用左右手和左右肩武器攻击测试目标。
5. 检查枪口、弹体、尾迹、导弹飞行和命中阶段是否优先生成 `NativeFXR_*` 对象，而不是旧占位 Renderer。
