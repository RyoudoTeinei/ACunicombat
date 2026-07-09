# AC6 本地案例资产准备

这个仓库不提交 AC6 的原始模型、贴图、骨架或动画文件。那些文件属于游戏资源，不能作为仓库内容分发。

仓库里提交的是可复现的本地案例清单和 Unity Editor 准备器：

- 清单：`E:\UnityDev\ACunicombat\Tools\AssetManifests\AC6_Melander1010_LocalCase.json`
- 准备器：`E:\UnityDev\ACunicombat\Assets\_Project\Editor\AC6LocalSampleCasePreparer.cs`
- 本地输出：`E:\UnityDev\ACunicombat\Assets\_Project\LocalSamples\AC6MelanderCase\Raw`

`Raw`、`Generated`、`Reports` 目录已在 `.gitignore` 中忽略。它们只应该存在于本机。

## 使用方法

1. 确认本机有解包资源：`E:\UnityDev\Resources\AC6_ExtractedAssets`。
2. 在 Unity 里执行菜单：
   - `Tools > ACunicombat > AC6 > Prepare Local Melander Case Assets`
3. 工具会按清单复制：
   - `HD_M_1010`
   - `BD_M_1010`
   - `AM_M_1010`
   - `LG_M_1010`
   - `WP_0010`
   - `WP_1310`
   - `WP_1410`
   - c0000 `Skeleton.hkx`
   - c0000 `a000_000000.hkx`
   - c0000 `a000_010900.hkx`

如果解包资源不在默认路径，可以设置环境变量：

```powershell
$env:AC6_EXTRACTED_ASSETS="D:\YourPath\AC6_ExtractedAssets"
```

## 这个案例的目的

这套案例不是为了把游戏资源发布出去，而是为了让导入管线有固定的本地测试输入：

- frame 部件能验证 FLVER 权重骨、局部 AP 骨、c0000 公共骨之间的关系。
- weapon 部件能继续验证 Blender baked texture 管线。
- c0000 skeleton 和 HKX 动作能验证原始动画重定向。

对外咨询或代码审查时，只推送清单、工具和诊断结论；不要推送 `Raw` 目录里的实际资源。
