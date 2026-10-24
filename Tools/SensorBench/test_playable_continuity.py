"""Regression tests for the independent gameplay trace judge itself."""
import contextlib,io,json,pathlib,tempfile,unittest
from analyze_playable_continuity import analyze,candidate,project,uncertainty_consistent_update,matching_truth_pool

def v_position(d):return tuple(d['position'][a] for a in ('x','y','z'))
def xyz(x=0,y=0,z=100):return dict(x=x,y=y,z=z)
def truth(entity="a",team=1,x=0):
 return dict(kind="truth",entity=entity,owner="Player_1" if team==1 else "Enemy_1",team=team,track=100 if team==1 else 0,position=xyz(x),renderPosition=xyz(x),velocity=xyz(0,0,0),alive=True,ownship=team==1)
def fused(track=100,x=0,affiliation="Friendly"):
 return dict(kind="fused",track=track,position=xyz(x),velocity=xyz(0,0,0),acceleration=xyz(0,0,0),affiliation=affiliation,evidenceTime=0,uncertainty=1)
def marker(track=100,x=0,entity="a",style="green",source="ownship_fallback"):
 return dict(kind="hud",track=track,entity=entity,position=xyz(x),screen=dict(x=500,y=500),style=style,source=source)
def judge(*groups):
 with tempfile.TemporaryDirectory() as folder:
  path=pathlib.Path(folder)
  rows=[]
  for i,group in enumerate(groups):
   for row in [dict(kind="frame"),dict(kind="hud_frame"),*group]:rows.append(dict(row,frame=i,time=i/60))
  (path/"continuity.jsonl").write_text("\n".join(map(json.dumps,rows)))
  with contextlib.redirect_stdout(io.StringIO()):return analyze(path)

class ContinuityJudgeTests(unittest.TestCase):
 def test_short_clean_clip_cannot_pass_full_gameplay_coverage(self):
  result=judge([truth(),fused(),marker()],[truth(),fused(),marker()])
  self.assertTrue(result["continuity_pass"]);self.assertFalse(result["passed"])
 def test_duplicate_marker_is_not_hidden_by_same_truth(self):
  result=judge([truth(),fused(),marker(),marker(200,entity="",style="amber",source="fused_nonfriendly")])
  self.assertEqual(result["metrics"]["duplicate_marker_frames"],1)
  self.assertFalse(result["continuity_pass"])
 def test_friendly_identity_loss_is_rejected(self):
  result=judge([truth(),fused(),marker()],[truth(),fused(affiliation="Unknown"),marker()])
  self.assertEqual(result["anomalies"]["friendly_identity_lost"],1)
 def test_hostile_green_marker_is_rejected(self):
  result=judge([truth(team=2),fused(),marker()])
  self.assertEqual(result["metrics"]["hostile_green_frames"],1)
 def test_unexplained_position_jump_is_rejected(self):
  result=judge([fused()],[fused(x=3000)])
  self.assertEqual(result["anomalies"]["position_jump"],1)
 def test_crossing_opposite_velocity_is_not_associated(self):
  a=truth();a["velocity"]=xyz(1000,0,0)
  b=fused();b["velocity"]=xyz(-1000,0,0)
  self.assertIsNone(candidate(b,[a]))
 def test_close_ambiguous_targets_are_not_assigned_arbitrarily(self):
  self.assertIsNone(candidate(fused(),[truth("a",x=10),truth("b",x=20)]))
 def test_coarse_update_requires_same_unique_target_inside_both_uncertainties(self):
  a=fused(x=3000);a['uncertainty']=2000
  b=fused(x=-1000);b['uncertainty']=1000
  self.assertIsNotNone(uncertainty_consistent_update(a,b,v_position(a),[truth()]))
  self.assertIsNone(uncertainty_consistent_update(a,b,v_position(a),[truth(),truth("b",x=100)]))
 def test_precise_jump_cannot_be_excused_as_noisy_correction(self):
  a=fused();b=fused(x=3000)
  self.assertIsNone(uncertainty_consistent_update(a,b,v_position(a),[truth()]))
 def test_correction_to_another_target_still_fails(self):
  a=fused(x=10000);a['uncertainty']=1000
  b=fused();b['uncertainty']=1000
  self.assertIsNone(uncertainty_consistent_update(a,b,v_position(a),[truth(),truth("b",x=10000)]))
 def test_doppler_distinguishes_crossing_missiles_in_coarse_position_volume(self):
  a=fused(x=0);a['uncertainty']=1000;a['velocity']=xyz(0,0,1000)
  b=fused(x=1000);b['uncertainty']=1000;b['velocity']=xyz(0,0,1000)
  outgoing=truth("a");outgoing['velocity']=xyz(0,0,1000)
  incoming=truth("b");incoming['velocity']=xyz(0,0,-1000)
  self.assertIsNotNone(uncertainty_consistent_update(a,b,v_position(a),[outgoing,incoming],observer_position=(0,0,-10000)))
  b['velocity']=xyz(0,0,-1000)
  self.assertIsNone(uncertainty_consistent_update(a,b,v_position(a),[outgoing,incoming],observer_position=(0,0,-10000)))
 def test_ended_track_is_not_reassigned_to_a_different_live_missile(self):
  ended=truth("ended",team=2,x=0);other=truth("other",team=2,x=900)
  result=judge([ended,other],[other,fused(101,0,"Unknown"),fused(102,900,"Unknown"),marker(101,0,"","amber","fused_nonfriendly"),marker(102,900,"","amber","fused_nonfriendly")])
  self.assertEqual(result['metrics'].get('duplicate_marker_frames',0),0)
  self.assertEqual(result['metrics']['ended_track_coast_frames'],1)
 def test_ended_truth_does_not_hide_a_real_duplicate_on_a_live_missile(self):
  ended=truth("ended",team=2,x=0);other=truth("other",team=2,x=900)
  result=judge([ended,other],[other,fused(101,0,"Unknown"),fused(102,900,"Unknown"),fused(103,900,"Unknown"),marker(101,0,"","amber","fused_nonfriendly"),marker(102,900,"","amber","fused_nonfriendly"),marker(103,900,"","amber","fused_nonfriendly")])
  self.assertEqual(result['metrics']['duplicate_marker_frames'],1);self.assertFalse(result['continuity_pass'])
 def test_live_target_identity_switch_still_fails_with_ended_truth_history(self):
  ended=truth("ended",team=2,x=-900);a=truth("a",team=2,x=0);b=truth("b",team=2,x=900)
  result=judge([ended,a,b,fused(101,0,"Unknown")],[a,b,fused(101,900,"Unknown")])
  self.assertEqual(result['anomalies']['track_changed_target'],1);self.assertFalse(result['continuity_pass'])
 def test_ended_truth_history_is_bounded_by_existing_display_coast(self):
  old=dict(truth("ended",team=2),time=0);history={}
  matching_truth_pool([old],history,0)
  self.assertEqual(len(matching_truth_pool([],history,1.49)),1)
  self.assertEqual(matching_truth_pool([],history,1.51),[])

 def test_projection_obeys_camera_and_hud_scale(self):
  cam=dict(clip=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],width=200,height=100,scale=.5)
  self.assertEqual(project((0,0,0),cam),(200,100))

if __name__=="__main__":unittest.main()
