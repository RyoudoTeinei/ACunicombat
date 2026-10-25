# 地图尾迹卡死与Canvas索引修复

2026-10-25，用户反馈Play几十秒后打开M地图、切自由攻击时Unity失去响应，只能任务管理器结束。

发现MechBattlefieldMap.DrawTrail的while循环可能因float舍入不前进。具体walked=4.904900074005127、clearance=14.572131156921387，offset=clearance-walked得到9.667230606079102，walked+offset却为14.57213020324707，仍小于clearance，旧分支重复赋值同一offset，无法退出。此为确定可复现的代码卡死点，与用户触发过程吻合；没有崩溃转储能证明此前每次卡死都由同一点引发。

改为最多11个整数虚线编号求交，避免浮点推进循环。保留留白、虚线周期与历史轨迹，不修改传感器航迹或身份融合。

同次日志反复出现CanvasRenderer expects meshes with 16 bit indices。MechTacticalVectorLayer原先显式创建UInt32网格，现改UInt16，并以60000顶点为一批创建复用子层，保留全部几何；增加非有限坐标过滤及EditMode网格销毁处理。

验证：Artifacts/UnityHang-20261025/results2.xml，50项全部通过。大网格测试80000顶点/120000索引分成两批；下一帧少量几何时旧批次隐藏。完整可玩场景35秒开地图、40秒通过SetWeaponPolicy切自由攻击，运行至100秒正常退出，日志无Canvas索引错误。心跳内存约2.5—2.59 GB；本轮无导弹发射，不属于密集导弹实战验收。

原日志保留在Artifacts/UnityHang-20261025的.before文件；汇总review.html，浮点证据loop-proof.json，场景心跳soak/heartbeat.txt和截图soak/map.png。
