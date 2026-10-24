import json, pathlib, tempfile, unittest
from evaluate import load_rows, validate_alignment, interval

class EvaluatorIntegrityTests(unittest.TestCase):
    def test_empty_log_is_not_success(self):
        with tempfile.TemporaryDirectory() as d:
            p=pathlib.Path(d)/'empty.jsonl';p.write_text('')
            with self.assertRaises(ValueError):load_rows(p)
    def test_duplicate_samples_are_rejected(self):
        row=dict(schemaVersion=1,seed=1,condition='control',observer=0,step=1,time=.04)
        with tempfile.TemporaryDirectory() as d:
            p=pathlib.Path(d)/'duplicate.jsonl';p.write_text((json.dumps(row)+'\n')*2)
            with self.assertRaises(ValueError):load_rows(p)
    def test_nonfinite_json_is_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            p=pathlib.Path(d)/'invalid.jsonl';p.write_text('{"time":NaN}')
            with self.assertRaises(ValueError):load_rows(p)
    def test_truncated_channel_is_rejected(self):
        spec=dict(durationSeconds=.04,stepSeconds=.04,seeds=[1],observerCount=1)
        with self.assertRaises(ValueError):validate_alignment(spec,{'tracks':{}})
    def test_future_sample_is_rejected(self):
        spec=dict(durationSeconds=.04,stepSeconds=.04,seeds=[1],observerCount=1)
        rows={(1,c,0,1):dict(time=.08) for c in ('control','jammed')}
        with self.assertRaises(ValueError):validate_alignment(spec,{'truth':rows})
    def test_single_seed_does_not_claim_confidence_interval(self):
        self.assertIsNone(interval([1]))
        self.assertEqual(interval([1,1,1]),[1,1])

if __name__=='__main__':unittest.main()