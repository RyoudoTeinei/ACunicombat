"""Offline single-target evaluator. Truth never returns to the production observer.
Only stdlib is required. It deliberately does not claim image recognition or full EW.
"""
import argparse, collections, csv, hashlib, html, json, math, pathlib, random, statistics, sys
import xml.etree.ElementTree as ET


class ContractViolation(ValueError):
    pass


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'), parse_constant=lambda x: (_ for _ in ()).throw(ValueError(x)))


def load_rows(path):
    result = {}
    for number, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        row = json.loads(line, parse_constant=lambda x: (_ for _ in ()).throw(ValueError(x)))
        key = (row['seed'], row['condition'], row['observer'], row['step'])
        if key in result:
            raise ValueError(f'{path.name}:{number}: duplicate sample {key}')
        if row['schemaVersion'] != 1 or not math.isfinite(row['time']):
            raise ValueError('Unsupported row schema or invalid time')
        result[key] = row
    if not result:
        raise ValueError(f'{path.name}: empty data is not success')
    return result


def validate_alignment(spec, channels):
    steps = round(spec['durationSeconds'] / spec['stepSeconds'])
    expected = {(seed, condition, observer, step) for seed in spec['seeds']
                for condition in ('control', 'jammed') for observer in range(spec['observerCount'])
                for step in range(1, steps + 1)}
    for name, rows in channels.items():
        if set(rows) != expected:
            raise ValueError(f'{name}: missing or unexpected samples ({len(rows)} / {len(expected)})')
        for key, row in rows.items():
            if abs(row['time'] - key[3] * spec['stepSeconds']) > 1e-6:
                raise ValueError(f'{name}: sample timestamp mismatch at {key}')


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[round((len(ordered) - 1) * fraction)]


def interval(values):
    # Independent seed means, never individual frames, are bootstrap units.
    if len(values) < 2:
        return None
    rng = random.Random(1729)
    estimates = [statistics.mean(rng.choices(values, k=len(values))) for _ in range(1000)]
    return [percentile(estimates, .025), percentile(estimates, .975)]


def evaluate(directory):
    spec = read_json(directory / 'scenario.json')
    manifest = read_json(directory / 'manifest.json')
    digest = hashlib.sha256((directory / 'scenario.json').read_bytes()).hexdigest()
    if digest.lower() != manifest['scenarioHash'].lower():
        raise ValueError('Scenario changed after execution manifest was created')
    result_xml = ET.parse(directory / 'test-results.xml').getroot()
    if result_xml.get('result') != 'Passed' or int(result_xml.get('total', '0')) == 0:
        raise ValueError('Unity tests did not complete successfully; partial data cannot pass')
    if not any('RunConfiguredScenario' in t.get('fullname', '') and t.get('result') == 'Passed'
               for t in result_xml.iter('test-case')):
        raise ValueError('Requested scenario was not executed')
    channels = {name: load_rows(directory / (name + '.jsonl'))
                for name in ('truth', 'observations', 'tracks', 'decisions')}
    validate_alignment(spec, channels)
    truth, observations, tracks, decisions = (channels[n] for n in ('truth', 'observations', 'tracks', 'decisions'))
    groups = collections.defaultdict(list)
    time_rows = []
    for key, true in truth.items():
        obs, track, decision = (channels[n][key] for n in ('observations', 'tracks', 'decisions'))
        if obs['kind'] not in ('measurement', 'no_detection') or track['kind'] not in ('track', 'no_track'):
            raise ValueError('Unexpected observation/track record')
        detected = obs['kind'] == 'measurement'
        if detected != obs['detected'] or (detected and not true['covered']):
            raise ContractViolation('Detection coverage contract violated')
        has_track = track['kind'] == 'track'
        if has_track and (track['lastUpdate'] > true['time'] + 1e-6 or track['lastPredict'] > true['time'] + 1e-6):
            raise ContractViolation('Track contains future data')
        if decision['authorized'] and not has_track:
            raise ContractViolation('No track but weapon release was authorized')
        pos_error = math.dist(track['position'], true['position']) if has_track else None
        vel_error = math.dist(track['velocity'], true['velocity']) if has_track else None
        angle_error = None
        if detected:
            dot = sum(a*b for a, b in zip(obs['direction'], true['direction']))
            angle_error = math.degrees(math.acos(max(-1., min(1., dot))))
        sample = dict(time=true['time'], covered=true['covered'], detected=detected, hasTrack=has_track,
                      authorized=decision['authorized'], positionErrorM=pos_error, velocityErrorMps=vel_error,
                      rangeErrorM=(obs['range'] - true['range']) if detected else None,
                      radialVelocityErrorMps=(obs['radialVelocity'] - true['radialVelocity']) if detected else None,
                      angleErrorDeg=angle_error, reason=decision['reason'])
        for value in sample.values():
            if isinstance(value, float) and not math.isfinite(value):
                raise ValueError('Nonfinite metric')
        phase = 'before' if true['time'] < spec['jamStartSeconds'] else ('during' if true['time'] < spec['jamEndSeconds'] else 'after')
        if true['time'] >= spec['warmupSeconds']:
            groups[(key[0], key[1], key[2], phase)].append(sample)
        time_rows.append(dict(seed=key[0], condition=key[1], observer=key[2], step=key[3], **sample))
    # Shared sensor loop must not let jammer A change observer B's actual input or decision.
    for name in ('observations', 'tracks', 'decisions'):
        for key, row in channels[name].items():
            if key[1] != 'control' or key[2] == 0:
                continue
            peer = channels[name][(key[0], 'jammed', key[2], key[3])]
            if {k:v for k,v in row.items() if k != 'condition'} != {k:v for k,v in peer.items() if k != 'condition'}:
                raise ContractViolation(f'Observer isolation failed: {name}, {key}')
    trials = []
    for (seed, condition, observer, phase), rows in sorted(groups.items()):
        covered = sum(r['covered'] for r in rows)
        if not covered:
            raise ValueError('No covered opportunities; cannot claim a detection rate')
        item = dict(seed=seed, condition=condition, observer=observer, phase=phase, samples=len(rows),
                    covered=covered, detectionRate=sum(r['detected'] for r in rows)/covered,
                    trackAvailability=sum(r['hasTrack'] for r in rows)/len(rows),
                    releaseRate=sum(r['authorized'] for r in rows)/len(rows),
                    missingTracks=sum(not r['hasTrack'] for r in rows),
                    rejectionReasons=dict(collections.Counter(r['reason'] for r in rows if not r['authorized'])))
        for field in ('positionErrorM', 'velocityErrorMps', 'rangeErrorM', 'radialVelocityErrorMps', 'angleErrorDeg'):
            values = [r[field] for r in rows if r[field] is not None]
            item[field+'Rmse'] = math.sqrt(statistics.mean(v*v for v in values)) if values else None
            item[field+'Samples'] = len(values)
        trials.append(item)
    summary = []
    for condition in ('control', 'jammed'):
        for observer in range(spec['observerCount']):
            for phase in ('before', 'during', 'after'):
                rows = [r for r in trials if (r['condition'],r['observer'],r['phase']) == (condition,observer,phase)]
                item = dict(condition=condition, observer=observer, phase=phase, independentSeeds=len(rows))
                for field in ('detectionRate','trackAvailability','releaseRate'):
                    values=[r[field] for r in rows]
                    item[field]=statistics.mean(values)
                    item[field+'CI95']=interval(values)
                for field in ('positionErrorMRmse','velocityErrorMpsRmse','rangeErrorMRmse','radialVelocityErrorMpsRmse','angleErrorDegRmse'):
                    values=[r[field] for r in rows if r[field] is not None]
                    item[field]=math.sqrt(statistics.mean(v*v for v in values)) if values else None
                summary.append(item)
    platform_recording = None
    live_path = directory / 'platform-observers.jsonl'
    if live_path.exists():
        live = [json.loads(line) for line in live_path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
        if not live:
            raise ValueError('Playable AI observer log is empty')
        by_observer = collections.defaultdict(list)
        for row in live:
            by_observer[row['observer']].append(row)
        platform_recording = []
        for observer, rows in sorted(by_observer.items()):
            platform_recording.append(dict(observer=observer, records=len(rows),
                states=dict(collections.Counter(r['state'] for r in rows)),
                selectedTrackRecords=sum(r['hasSelectedTrack'] for r in rows),
                maxConsumedAirInputs=max(len(r.get('airInputs', [])) for r in rows),
                maxConsumedMissileInputs=max(len(r.get('missileInputs', [])) for r in rows),
                maxConsumedWarningInputs=max(len(r.get('warningInputs', [])) for r in rows)))
    metrics = dict(schemaVersion=1, scenarioId=spec['scenarioId'], contractGate='Passed',
        capabilityGate='Inconclusive', overall='Inconclusive',
        explanation='Performance measured; gameplay capability thresholds are not yet calibrated or approved.',
        scope='Single-target open-loop radar + per-observer tracks + production fire-control solver. No tactical AI execution.',
        trials=trials, summary=summary, platformRecording=platform_recording)
    if platform_recording is not None:
        metrics['scope'] = 'Controlled radar comparison plus recorded production AI inputs from the playable scene. Tactical decision quality is not scored.'
    (directory/'metrics.json').write_text(json.dumps(metrics, indent=2, allow_nan=False), encoding='utf-8')
    with (directory/'timeseries.csv').open('w', newline='', encoding='utf-8') as f:
        writer=csv.DictWriter(f,fieldnames=list(time_rows[0]));writer.writeheader();writer.writerows(time_rows)
    with (directory/'summary.csv').open('w', newline='', encoding='utf-8') as f:
        writer=csv.DictWriter(f,fieldnames=list(summary[0]));writer.writeheader();writer.writerows(summary)
    capabilities = dict(Radar='Passed',Tracking='Passed',ReceiverInjection='Passed',FireControl='Passed',
        ObserverIsolation='Passed',EndToEndEmitter='NotImplemented',RenderedEO='NotImplemented',
        Identification='NotImplemented',TacticalAI='NotImplemented',WeaponOutcome='NotImplemented')
    if platform_recording is not None:
        capabilities['TacticalAI']='Inconclusive'
        capabilities['ProductionAIInputRecording']='Passed'
        capabilities['PlayableSceneSmoke']='Passed'
    (directory/'capabilities.json').write_text(json.dumps(capabilities,indent=2),encoding='utf-8')
    def fmt(value): return '—' if value is None else f'{value:.4g}' if isinstance(value,float) else str(value)
    fields=('condition','observer','phase','independentSeeds','detectionRate','trackAvailability','releaseRate','positionErrorMRmse','velocityErrorMpsRmse')
    table='<tr>'+''.join('<th>'+html.escape(k)+'</th>' for k in fields)+'</tr>'
    table+=''.join('<tr>'+''.join('<td>'+html.escape(fmt(row[k]))+'</td>' for k in fields)+'</tr>' for row in summary)
    platform_html = ''
    if platform_recording is not None:
        platform_html = '<h2>真实场景：AI 实际消费的信息</h2><p>下表来自正式 AI 内保存的输入快照；读取日志不会触发传感器或融合。状态和输入数量已记录，战术选择优劣尚未评分。</p><pre>' + html.escape(json.dumps(platform_recording, ensure_ascii=False, indent=2)) + '</pre>'
        platform_html += '<p><a href="platform-observers.jsonl">AI 输入与状态</a> · <a href="platform-truth.jsonl">隔离裁判真值</a> · <a href="platform-baseline.json">启动、运动、发射及告警结果</a></p>'
        for name, label in [('playable-scene.png','普通视野'),('playable-thermal.png','热像视野'),('EdgePyramidDepthViews.png','HUD 指示器')]:
            if (directory/name).exists():
                platform_html += f'<figure><figcaption>{label}</figcaption><img style="max-width:100%" src="{name}"></figure>'
        platform_html += '<p>真实场景记录未套用单目标纯仿真误差评分；捕获时间、仿真时间、证据时间分别保留。方位告警没有虚构距离。下面的误差表只针对受控雷达对照。</p>'
    page=f'''<!doctype html><meta charset="utf-8"><title>SensorBench</title>
<style>body{{font:16px system-ui;background:#101822;color:#dbe7ef;padding:32px;max-width:1400px;margin:auto}}td,th{{padding:9px;border-bottom:1px solid #334;text-align:left}}th{{color:#8dcce8}}a{{color:#8dcce8}}.notice{{color:#ffd787}}p{{max-width:950px;line-height:1.6}}</style>
<h1>{html.escape(spec['scenarioId'])}</h1><p class="notice">ContractGate: Passed · CapabilityGate: Inconclusive</p>
<p>{html.escape(metrics['explanation'])}</p><p>{html.escape(metrics['scope'])}</p>
<p>Only observer 0 receives injected interference. Observer 1 is checked for exact isolation. Measurements and truth are joined offline at the same sample time. Error metrics are conditional on available measurements/tracks; absence is reported separately.</p>
{platform_html}<h2>受控雷达对照</h2><table>{table}</table><h2>Coverage</h2><pre>{html.escape(json.dumps(capabilities,indent=2))}</pre>
<p>Rates are means over independent seeds. Bootstrap intervals and per-seed RMSE are in <a href="metrics.json">metrics.json</a>. No image-based recognition, full EW propagation, tactical decision-quality score, or actual hit-rate validation is claimed.</p>
<p><a href="timeseries.csv">Per-observer timeline</a> · <a href="summary.csv">Summary</a> · <a href="manifest.json">Reproduction manifest</a> · <a href="test-results.xml">Unity test results</a></p>'''
    if (directory / 'platform-actions.jsonl').exists():
        page += '<h2>装备执行反馈</h2><p><a href="platform-actions.jsonl">命令、预留与最终执行结果</a></p>'
    if (directory / 'platform-support.jsonl').exists():
        page += '<p><a href="platform-support.jsonl">持续制导支持反馈</a></p>'
    if (directory / 'part-damage-demo.json').exists():
        page += '<h2>单一总 AP 与部件功能状态</h2><p><a href="part-damage-demo.json">实际伤害与效能记录</a>：单个装备失效时整机仍存活。</p><div style="display:flex;gap:12px;flex-wrap:wrap">'
        for name, label in [('normal','正常/轻损'),('impaired','受损影响功能'),('severe','重损仍可用'),('destroyed','完全失效，红框空心')]:
            page += '<figure><img width="256" src="damage-hud-' + name + '.png"><figcaption>' + label + '</figcaption></figure>'
        page += '</div>'
    if (directory / 'thermal-source-demo.json').exists():
        heat = json.loads((directory / 'thermal-source-demo.json').read_text(encoding='utf-8'))
        page += '<h2>导弹燃尽：共同热源</h2><p>来自真实弹体固定物理步测试。热像点源温度与红外信号读取同一热状态，燃尽后保留余热；数值为游戏模型，未做物理辐射标定。</p><pre>' + html.escape(json.dumps(heat, ensure_ascii=False, indent=2)) + '</pre><p><a href="thermal-source-demo.json">热源记录</a> · <a href="test-results.xml">主动防御、物理拦截与区域装甲测试</a></p>'
    if (directory / 'defense-extension-demo.json').exists():
        defense = json.loads((directory / 'defense-extension-demo.json').read_text(encoding='utf-8'))
        page += '<h2>APS 扩展组件实际拦截</h2><p>受控本机航迹输入，经装备裁决消耗弹药、生成独立弹射物，并以物理爆炸毁伤拦截。选择目标和排队不会删除来袭导弹；此例不代表统计命中率。</p><pre>' + html.escape(json.dumps(defense, ensure_ascii=False, indent=2)) + '</pre><p><a href="defense-extension-demo.json">拦截记录</a></p>'
    (directory/'report.html').write_text(page,encoding='utf-8')
    return metrics


def main():
    parser=argparse.ArgumentParser();parser.add_argument('output',type=pathlib.Path);args=parser.parse_args()
    try:
        metrics=evaluate(args.output)
        print(json.dumps({k:metrics[k] for k in ('contractGate','capabilityGate','overall')}))
        return 3
    except ContractViolation as error:
        (args.output/'evaluation-error.json').write_text(json.dumps(dict(status='Failed',reason=str(error))),encoding='utf-8')
        print(str(error),file=sys.stderr);return 1
    except (OSError,ValueError,KeyError,TypeError,ET.ParseError) as error:
        (args.output/'evaluation-error.json').write_text(json.dumps(dict(status='InfrastructureFailed',reason=str(error))),encoding='utf-8')
        print(str(error),file=sys.stderr);return 2

if __name__ == '__main__': sys.exit(main())