"""Summarize fleet checks separately from the unchanged playable continuity gate."""
import argparse, collections, html, json, pathlib, re, xml.etree.ElementTree as ET
ap=argparse.ArgumentParser();ap.add_argument('root',type=pathlib.Path);ap.add_argument('--tests',nargs='+',type=pathlib.Path,required=True);args=ap.parse_args();root=args.root.resolve()
def link(path,label):
    return '<a href="'+html.escape(path.resolve().relative_to(root).as_posix(),quote=True)+'">'+html.escape(label)+'</a>'
latest={};tests=[]
for file in args.tests:
    doc=ET.parse(file).getroot();tests.append((file,doc.attrib))
    for case in doc.iter('test-case'):
        key=case.get('fullname');at=case.get('end-time','')
        if key and (key not in latest or at>=latest[key]['end']):latest[key]={'result':case.get('result'),'end':at,'source':file.resolve().relative_to(root).as_posix()}
ledger={'note':'Latest result per unique test case; this is a provenance ledger, not a synthetic Unity test run.','unique_cases':len(latest),'outcomes':dict(collections.Counter(c['result'] for c in latest.values())),'cases':latest}
(root/'latest-case-ledger.json').write_text(json.dumps(ledger,ensure_ascii=False,indent=2),encoding='utf-8')
rows=[];failed=False
for file in sorted(root.rglob('fleet-acceptance.txt')):
    folder=file.parent;acceptance=folder/'acceptance.json'
    if not acceptance.exists():continue
    check=file.read_text(encoding='utf-8-sig');result=json.loads(acceptance.read_text(encoding='utf-8-sig'));log=(folder/'unity.log').read_text(encoding='utf-8',errors='replace')
    launched=log.count('Interceptor launched at');destroyed=len(re.findall(r'Warhead damaged missile:.*destroyed=True',log));fleet='PASS=True' in check
    passed=result.get('passed',False);failed|=not passed or not fleet
    events=', '.join(f'{k}: {v}' for k,v in result.get('anomalies',{}).items() if k!='uncertainty_consistent_update') or '无'
    rows.append('<tr><td>'+html.escape(folder.relative_to(root).as_posix())+'</td><td>'+str(result.get('frames','?'))+' 帧 / '+str(result.get('missiles','?'))+' 枚导弹</td><td class="'+('ok' if fleet else 'bad')+'">'+('通过' if fleet else '失败')+'</td><td>'+str(launched)+' / '+str(destroyed)+'</td><td class="'+('ok' if passed else 'bad')+'">'+('通过' if passed else '失败')+'<br>'+html.escape(events)+'</td><td>'+link(file,'编队检查')+' · '+link(acceptance,'原连续性判定')+' · '+link(folder/'build-manifest.json','版本记录')+'</td></tr>')
status='未通过完整验收' if failed or not rows else '通过当前覆盖范围'
testrows=''.join('<tr><td>'+link(f,f.name)+'</td><td>'+str(d.get('total'))+'</td><td>'+str(d.get('passed'))+'</td><td>'+str(d.get('failed'))+'</td></tr>' for f,d in tests)
page="""<!doctype html><meta charset="utf-8"><title>P5/P6 编队实施与联合验收</title><style>body{margin:30px auto;padding:0 24px;max-width:1400px;background:#101820;color:#e8eff3;font:16px/1.65 system-ui}a{color:#69caff}h1{font-size:30px}h2{margin-top:32px}table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:12px;border:1px solid #344650;font-size:14px}.bad{color:#ffad85}.ok{color:#80e3ae}.box{padding:18px;background:#182832;border-radius:8px}code{background:#263642;padding:2px 5px}</style><h1>P5/P6：编队指挥、装备执行与协同防御</h1><div class="box"><strong class="bad">STATUS</strong><p>命令和防御功能检查，与雷达/导弹画面连续性分别判定。保留 v4 / 3σ 标准、原始失败、录像和日志；不以武器已发射代替实际拦截成功，不以功能覆盖代替整体验收。</p></div><h2>本批接入</h2><p>玩家长机、AI 长机/僚机与显式指挥权限；确认攻击/自由攻击；指定装备和目标；单机脱队任务与实际推进归队；装备实例动态遍历；共同动作裁决和实际开火反馈；经过物理数据链的心跳、装备能力、命令回执、威胁、预约与发射投入状态。</p><p>移动只保留接口。未修改 M 地图和 IFF 问询应答实现。自动长机接替和最终平衡不在本批完成声明内。</p><h2>操作</h2><p>在 <code>AC6PlayerControl</code> 场景按 <b>F6</b> 打开独立编队面板。↑↓ 选择机体，←→ 选择武器，Enter 攻击当前雷达候选目标，F7 切换编队攻击规则，Backspace 归队。没有已收到的装备报告时不能假装知道他机装备。</p><h2>自动回归</h2><p>LEDGER。按用例最近一次结果汇总；下表仍保留最初含失败的 411 项相关回归运行。六槽测试曾因 Unity 测试域重载后的闭包引用失败，修正夹具后重跑；未删除断言。</p><table><tr><th>真实 Unity 结果文件</th><th>总数</th><th>通过</th><th>失败</th></tr>TESTROWS</table><p>LEDGERLINK</p><h2>完整可玩场景</h2><p>真实 AI、正常雷达调度与武器链；脚本通过玩家输入/指挥接口发出操作。防御变体使用同一正式场景，把队友布置在近处并通过正常装配安装现有拦截器。场景不保存。60/30 是模拟时间步，不是性能成绩。“拦截发射 / 摧毁”来自实际弹体发射与近炸毁伤记录。</p><table><tr><th>运行</th><th>覆盖</th><th>命令检查</th><th>拦截发射 / 近炸摧毁记录</th><th>原连续性判据</th><th>证据</th></tr>ROWS</table><p>REPLAY</p><h2>不能标记完成的部分</h2><p>密集导弹的底层航迹位置修正与归属变化仍触发原判据；较早的近距运行还出现重复标记和画面修正超限。因此不能把 IFF、雷达/导弹信息融合和 P5/P6 整体标成已完善。跨编队大规模权限树、长机接替、部件损坏/弹药耗尽下的完整编队重分配仍需扩大场景覆盖。</p>"""
page=page.replace('STATUS',status).replace('LEDGERLINK',link(root/'latest-case-ledger.json','逐用例结果与来源')).replace('LEDGER',f"最近结果覆盖 {len(latest)} 个不同用例：{html.escape(str(ledger['outcomes']))}").replace('TESTROWS',testrows).replace('ROWS',''.join(rows)).replace('REPLAY',link(root/'replay.html','逐帧回放与异常定位'))
(root/'review.html').write_text(page,encoding='utf-8');print(root/'review.html')
