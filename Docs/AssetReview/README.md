# 游戏资产与修改单工作流

入口：http://127.0.0.1:5187/assets.html 。也可双击项目根目录“启动游戏资产表.cmd”。网页沿用E:/UnityDev/Tools/AC6WikiEditor本地资料库服务。原AC6参考资料编辑器保留；当前游戏资产编辑申请用新入口。

## 数据边界

- 当前部件配置从AC6FullPartCatalog.asset及其Resources/AC6Parts资产实时读取，网页不写这些文件。
- 独立弹药及导弹仓兼容性来自ammunition.json，导出器只读取当前已编译Assembly-CSharp.dll中的运行时目录；不运行Unity场景。包含来源代码SHA256，代码变化后页面警告过期，并禁止基于过期快照完成弹药参数申请。
- 用户保存仅写Docs/AssetReview/requests.json；不得把网页“申请改为”当成已经实现的值。
- 完整字段可展开，基础/持续散布对外使用D80毫弧度。原每轴sigma只读。最大射程用米输入，界面标明单位。
- 左右型号是独立资产记录，当前申请关联所选资产ID；需要左右一起或跨型号修改时，可在留言或全局留言中注明范围。
- 未提交草稿存在本浏览器；只有点击保存后才进入项目文件、供agent读取。

## Agent处理流程

1. 用户说“留言已保存”并要求处理时，GET /api/asset-review/requests（或读取requests.json），关注pending/working/blocked，按最新revision处理，不凭旧会话猜测。
2. 读取对应游戏代码、资产和用户留言，必要时澄清；网页不会自动修改游戏或自动运行agent。
3. 实现并验证。若涉及独立弹药，先确保Unity已编译最新代码，再运行下面的只读导出器刷新快照。
4. POST /api/asset-review/requests/{id}/status，JSON包含status、evidence和expectedRevision。status支持working、blocked、pending、done。evidence写实际变更及测试结果/报告路径；不把未验证工作标记done。
5. done对每项结构化字段检查当前游戏值是否等于请求值；不符返回409。评论类任务由agent核对内容并记录证据。若处理期间用户修改了留言，旧revision的完成操作返回409，重新读取，不覆盖新要求。
6. 用户编辑尚未完成的单据会生成新历史版本并回到pending。已完成单据不可覆盖，后续变化新建单据。不得删除或改写历史来伪造完成。

## API

- GET /api/asset-review/catalog：当前313条记录（299部件+14弹药，数量随目录变化），完整fields和raw源数据。
- GET /api/asset-review/requests：持久化修改单及历史。
- POST /api/asset-review/requests：{id:null,assetId:"...",comment:"...",changes:[{field:"weaponPerformance.roundsPerMinute",before:"520",after:"600"}],expectedRevision:0}。全局留言assetId为空、changes为空。
- 修改已有单据提供id与当前expectedRevision。before保留最初申请的基准。
- POST /api/asset-review/requests/{id}/status：{status:"done",evidence:"实际实现与验证记录",expectedRevision:2}。

## 弹药快照刷新

工具：E:/UnityDev/Tools/AC6WikiEditor/AmmoSnapshotExporter/AmmoSnapshotExporter.csproj

使用Unity自带.NET SDK运行：

```powershell
& 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Data/DotNetSdk/dotnet.exe' run --project E:/UnityDev/Tools/AC6WikiEditor/AmmoSnapshotExporter/AmmoSnapshotExporter.csproj -- E:/UnityDev/ACunicombat
```

源码比编译程序集更新时会拒绝导出，不把旧DLL结果当成最新配置。

## 验证

Artifacts/AssetWorkbench-20261025/api-test-results.json记录隔离API测试：保存不写游戏、编辑历史、并发冲突、完成前实际值检查、完成记录不可覆盖、跨站写入拒绝、原子落盘。隔离浏览器验证保存620 RPM申请后实际值仍610、刷新后待办仍存在；这些是假工程测试数据，没有写入真实游戏或正式留言。

正式服务器不包含测试留言。已有历史调整可以在“既有配置变更记录”中查看原开发日志。
