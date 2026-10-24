"""Version 5: unchanged acceptance gates; retain ended truth briefly to avoid false pairing."""
import argparse,collections,json,math,pathlib,shutil

def v(d,k='position'):
 x=d.get(k,{}) or {};return tuple(x.get(a,0.) for a in ('x','y','z'))
def distance(a,b):return math.dist(a,b)
def project(point,camera):
 if not camera or len(camera.get('clip',[]))!=16:return None
 m=camera['clip'];a=(*point,1);q=[sum(m[r*4+c]*a[c] for c in range(4)) for r in range(4)]
 if q[3]<=.01:return None
 scale=camera.get('scale',1)
 return ((q[0]/q[3]*.5+.5)*camera['width']/scale,(.5-q[1]/q[3]*.5)*camera['height']/scale)
def render_position(truth):
 p=v(truth,'renderPosition');return p if any(p) else v(truth)
def candidate(sample,truths):
 ranked=[]
 for t in truths:
  pe=distance(v(sample),v(t));ve=distance(v(sample,'velocity'),v(t,'velocity'))
  # Never identify an incoming/outgoing pair as the same missile just because they cross.
  if pe<1000 and ve<max(400,3*sample.get('velocityUncertainty',0)):ranked.append((pe+ve*.25,t))
 ranked.sort(key=lambda x:x[0]);return ranked[0][1] if ranked and (len(ranked)==1 or ranked[1][0]-ranked[0][0]>100) else None

def matching_truth_pool(observations,history,now):
 # A sensor track may legitimately coast after its physical target ends. Removing
 # that target from the oracle immediately reassigns the track to a nearby live
 # missile and invents duplicate/screen-jump failures. Retention matches the
 # existing 1.5 s display-coast contract; no acceptance threshold changes here.
 live=[d for d in observations if d['alive']];live_ids={d['entity'] for d in live}
 for d in observations:history[d['entity']]=dict(d)
 result=list(live)
 for entity,previous in list(history.items()):
  age=now-previous['time']
  if age>1.5:del history[entity];continue
  if entity in live_ids or age<0:continue
  coast=dict(previous);coast['alive']=False
  coast['position']={axis:previous['position'][axis]+previous['velocity'][axis]*age for axis in ('x','y','z')}
  result.append(coast)
 return result

def uncertainty_consistent_update(old,current,prediction,truths,prior_binding=None,observer_position=None):
 # A Cartesian change is not itself a discontinuity: a kilometre-uncertain return
 # may legitimately move kilometres. Require BOTH estimates to contain the same
 # unique physical target, and a correction inside their combined uncertainty.
 # This diagnostic never establishes identity or suppresses a screen-space failure.
 before_sigma=old.get('uncertainty',0);after_sigma=current.get('uncertainty',0)
 if before_sigma<=0 or after_sigma<=0:return None
 def compatible(sample,truth):
  if observer_position is None:return True
  los=tuple(a-b for a,b in zip(v(sample),observer_position));length=math.sqrt(sum(a*a for a in los))
  if length<1:return False
  radial_error=abs(sum((a-b)*c/length for a,b,c in zip(v(sample,'velocity'),v(truth,'velocity'),los)))
  # Transverse velocity may still be unresolved in a new radar track. Doppler
  # distinguishes crossing inbound/outbound missiles without inventing that velocity.
  return radial_error<=max(400,4*sample.get('radialVelocityUncertainty',0))
 before=[t for t in truths if distance(prediction,v(t))<=3*before_sigma and compatible(old,t)]
 after=[t for t in truths if distance(v(current),v(t))<=3*after_sigma and compatible(current,t)]
 if len(before)!=1 or len(after)!=1 or before[0]['entity']!=after[0]['entity']:return None
 entity=after[0]['entity']
 if prior_binding is not None and any(t['entity']==prior_binding for t in truths) and prior_binding!=entity:return None
 bound=3*math.hypot(before_sigma,after_sigma)
 if distance(prediction,v(current))>bound:return None
 return dict(entity=entity,before_error_m=round(distance(prediction,v(after[0])),2),after_error_m=round(distance(v(current),v(after[0])),2),before_sigma_m=round(before_sigma,2),after_sigma_m=round(after_sigma,2))

def analyze(folder):
 frames={};bad=0;frame_count=0;player_frames=[];frame_times={}
 def read_groups():
  nonlocal bad
  current=None;group=collections.defaultdict(list)
  with (folder/'continuity.jsonl').open(encoding='utf-8-sig') as reader:
   for line in reader:
    try:d=json.loads(line)
    except json.JSONDecodeError:bad+=1;continue
    if current is not None and d['frame']<current:
     bad+=1;continue # chronological recorder contract; never silently reorder corrupt traces
    if current is not None and d['frame']!=current:
     yield current,group;group=collections.defaultdict(list)
    current=d['frame'];group[d['kind']].append(d)
   if current is not None:yield current,group
 events=[];truth_history={};last={};bindings={};truth_seen={};last_visual={};sources=collections.Counter();counts=collections.Counter();worst=collections.defaultdict(float);last_hud={};repaint_frames=[]
 for frame,group in read_groups():
  frame_count+=1;frames[frame]=group;player_frames.extend(group['frame'])
  camera=group['camera'][-1] if group['camera'] else None
  truths=[d for d in group['truth'] if d['alive']];live={d['entity']:d for d in truths};reports={d['track']:d for d in truths if d['track']};fused={d['track']:d for d in group['fused']};drawn=collections.Counter()
  frame_time=group['frame'][-1]['time'] if group['frame'] else max((d['time'] for d in group['truth']),default=0)
  frame_times[frame]=frame_time
  for expired in list(frames):
   if frame_time-frame_times[expired]>1:
    del frames[expired];del frame_times[expired]
  matching_truths=matching_truth_pool(group['truth'],truth_history,frame_time)
  visual_truth={d['entity']:d for d in group['visual_truth']}
  for d in truths:
   if d['entity'] in visual_truth:d['renderPosition']=visual_truth[d['entity']]['renderPosition']
   truth_seen[d['entity']]=d['owner']
  if group['hud_frame'] or group['hud']:repaint_frames.append(frame)
  for kind in ('raw','fused','hud'):
   dedupe=set()
   for d in group[kind]:
    identifier=d['track'] or d.get('entity');key=(kind,identifier);t=d['time'];p=v(d)
    if kind=='hud':
     dk=(identifier,d['source'])
     if dk in dedupe:continue
     dedupe.add(dk);sources[d['source']]+=1
    estimate=fused.get(d['track'],d) if kind=='hud' else d
    match=live.get(d.get('entity')) if kind=='hud' else None
    match=match or reports.get(d['track']) or candidate(estimate,matching_truths)
    prior_binding=bindings.get(key)
    if prior_binding in live and match is not None and prior_binding!=match['entity']:
     events.append(dict(kind='track_changed_target',stream=kind,frame=frame,time=t,track=d['track'],old=prior_binding,new=match['entity']))
    if match is not None:bindings[key]=match['entity']
    old=last.get(key)
    if old and 0<t-old['time']<.5:
     dt=t-old['time'];prediction=tuple(a+b*dt+.5*c*dt*dt for a,b,c in zip(v(old),v(old,'velocity'),v(old,'acceleration')))
     jump=distance(p,prediction)
     if kind=='hud':jump=max(0,distance(p,v(old))-math.sqrt(sum(a*a for a in v(estimate,'velocity')))*dt-100)
     worst[kind+'_correction_m']=max(worst[kind+'_correction_m'],jump)
     if jump>1000:
      evidence=uncertainty_consistent_update(old,d,prediction,truths,prior_binding,v(group['frame'][-1]) if group['frame'] else None) if kind!='hud' else None
      events.append(dict(kind='uncertainty_consistent_update' if evidence else 'position_jump',stream=kind,frame=frame,time=t,track=d['track'],metres=round(jump,2),uncertainty_check=evidence))
     if kind=='fused' and old.get('affiliation')=='Friendly' and d.get('affiliation')!='Friendly':events.append(dict(kind='friendly_identity_lost',frame=frame,time=t,track=d['track']))
    last[key]=d
    if kind!='hud' or match is None:continue
    if match['entity'] not in live:
     counts['ended_track_coast_frames']+=1;continue
    counts['judged_markers']+=1;drawn[match['entity']]+=1
    visual=last_visual.get(match['entity'])
    if visual and camera:
     previous_marker,previous_truth=visual;dt=t-previous_marker['time']
     if 0<dt<.5:
      # Remove actual physical target movement and current camera motion. What remains
      # is movement introduced by the presentation/estimation pipeline itself.
      offset=tuple(a-b for a,b in zip(v(previous_marker),render_position(previous_truth)))
      expected_world=tuple(a+b for a,b in zip(render_position(match),offset));expected_screen=project(expected_world,camera)
      if expected_screen:
       current=d.get('screen',{});pixels=math.hypot(current.get('x',0)-expected_screen[0],current.get('y',0)-expected_screen[1])
       focal=abs(camera.get('verticalProjection',math.sqrt(sum(camera['clip'][i]**2 for i in (4,5,6)))))*camera['height']/(2*camera.get('scale',1));degrees=math.degrees(math.atan2(pixels,max(1,focal)))
       worst['screen_correction_pixels']=max(worst['screen_correction_pixels'],pixels)
       if degrees>max(.1,30*dt):events.append(dict(kind='screen_correction',frame=frame,time=t,entity=match['entity'],pixels=round(pixels,2),degrees=round(degrees,3),seconds=round(dt,4)))
    last_visual[match['entity']]=(d,match)
    if match['team']==1 and d['style']!='green':counts['friendly_non_green_frames']+=1
    if match['team']!=1 and d['style']=='green':counts['hostile_green_frames']+=1
    prior=last_hud.get(match['entity'])
    if prior:
     dt=t-prior['time'];ps=prior.get('screen',{});ds=d.get('screen',{});sd=math.hypot(ds.get('x',0)-ps.get('x',0),ds.get('y',0)-ps.get('y',0))
     if .08<dt<.6 and sd<100:
      intermediate=[f for f in repaint_frames if prior['frame']<f<frame]
      expected=[]
      for f in intermediate:
       g=frames[f];truth=next((x for x in g['truth'] if x['entity']==match['entity'] and x['alive']),None)
       if truth is None:continue
       screen=truth.get('screen',{});cam=g['camera'][-1] if g['camera'] else None;w=cam['width']/cam.get('scale',1) if cam else 1920;h=cam['height']/cam.get('scale',1) if cam else 1080;inside=20<screen.get('x',-1)<w-20 and 20<screen.get('y',-1)<h-20
       observed=match['ownship'] or any(x['track']==d['track'] and x['time']-x['evidenceTime']<.6 for x in g['fused'])
       if inside and observed:expected.append(f)
      if len(expected)>=3:events.append(dict(kind='brief_marker_gap',frame=frame,time=t,entity=match['entity'],seconds=round(dt,3),rendered_missing=len(expected)))
    last_hud[match['entity']]=d
  for entity,n in drawn.items():
   if n>1:counts['duplicate_marker_frames']+=1
 event_counts=collections.Counter(e['kind'] for e in events);times=[d['time'] for d in player_frames]
 result=dict(judge_version=5,seconds=max(times,default=0)-min(times,default=0),frames=frame_count,repaint_frames=len(repaint_frames),missiles=len(truth_seen),launchers=sorted(set(truth_seen.values())),hud_draws=sum(sources.values()),sources=dict(sources),metrics=dict(counts),worst=dict(worst),anomalies=dict(event_counts),malformed_lines=bad)
 result['motion']={'maximum_speed_mps':max((math.sqrt(sum(a*a for a in v(d,'velocity'))) for d in player_frames),default=0),'lateral_input_frames':sum(abs(d.get('input',{}).get('x',0))>.1 for d in player_frames),'displacement_m':distance(v(player_frames[0]),v(player_frames[-1])) if player_frames else 0}
 result['screenshots']=len(list((folder/'frames').glob('*.png')))
 result['trace_integrity_pass']=bad==0
 result['coverage_pass']=result['repaint_frames']>=result['frames']*.9 and result['screenshots']>=result['seconds']*5 and result['motion']['maximum_speed_mps']>100 and result['motion']['lateral_input_frames']>100 and result['motion']['displacement_m']>1000 and result['seconds']>=110 and result['missiles']>=8 and len(result['launchers'])>=3 and result['hud_draws']>=100 and any(x.startswith('Player_') for x in result['launchers'])
 result['continuity_pass']=not any(event_counts[k] for k in ('position_jump','friendly_identity_lost','track_changed_target','brief_marker_gap','screen_correction')) and counts['hostile_green_frames']==0 and counts['duplicate_marker_frames']==0
 result['passed']=result['trace_integrity_pass'] and result['coverage_pass'] and result['continuity_pass']
 if (folder/'acceptance.json').exists():
  previous=json.loads((folder/'acceptance.json').read_text())
  version=previous.get('judge_version',1)
  if version<5 and not (folder/f'acceptance-v{version}.json').exists():
   shutil.copy2(folder/'acceptance.json',folder/f'acceptance-v{version}.json');shutil.copy2(folder/'anomalies.json',folder/f'anomalies-v{version}.json')
 (folder/'acceptance.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(folder/'anomalies.json').write_text(json.dumps(events,indent=2),encoding='utf-8');print(json.dumps(result,indent=2));return result
if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('folder',type=pathlib.Path);ap.add_argument('--report-only',action='store_true');a=ap.parse_args();r=analyze(a.folder);raise SystemExit(0 if r['passed'] or a.report_only else 1)
