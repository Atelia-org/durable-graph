"""Aggregate three independent A/B processes; each process compares both routes."""
import csv
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
processes = [json.loads((root / f"bench-{n}.json").read_text(encoding="utf-8-sig")) for n in (1, 2, 3)]
assert len({p['process'] for p in processes}) == 3
results = []
for units in (4096, 65536):
    for external in (False, True):
        for phase in ('edit', 'save', 'cycle', 'cold', 'first'):
            observations = []
            for p in processes:
                pair = [t for t in p['result'] if t['units'] == units]
                assert len(pair) == 2 and {t['external'] for t in pair} == {False, True}
                a, b = [t['chain'] for t in pair]
                assert len(a) == len(b) == 17
                assert all(r['Kind'] == 'Base' for r in [a[0],b[0]])
                assert all(r['ExactPredecessor'] for r in a+b)
                assert all(l == r and l['Kind'] == 'Delta' for l,r in zip(a[1:],b[1:])), 'the persisted Delta bytes differ by implementation'
                t = next(t for t in pair if t['external'] == external)
                if phase == 'first': sample = [t['firstSave']]
                elif phase == 'cold': sample = [t['cold']]
                elif phase == 'save': sample = t['saveCosts']
                elif phase == 'edit': sample = t['editCosts']
                else: sample = [{k:e[k]+s[k] for k in e} for e,s in zip(t['editCosts'], t['saveCosts'])]
                observations.append([statistics.median(r[k] for r in sample) for k in ['Microseconds','AllocatedBytes']])
            times, allocs = zip(*observations)
            results.append([units, 'registered' if external else 'builtin', phase, statistics.median(times), statistics.median(allocs), min(times), max(times)])
with (root / 'summary.csv').open('w',newline='') as f:
    writer=csv.writer(f)
    writer.writerow(['units','route','phase','median_us','median_thread_bytes','min_process_median_us','max_process_median_us'])
    writer.writerows(results)
for r in results: print(*r)

profile=json.loads((root/'diagnose.json').read_text(encoding='utf-8-sig'))['result']
with (root/'diagnostics.csv').open('w',newline='') as f:
    w=csv.writer(f)
    w.writerow(['units','route','base_encodes_first','base_encodes_hot','raw_base_bytes','typed_base_bytes','typed_base_count','hot_delta_count','hot_delta_total_body_bytes','first_store_bytes','hot_store_growth','read_edit_save_thread_bytes','isolated_33pieces_delta_bytes','isolated_base_thread_bytes','isolated_delta_thread_bytes','isolated_replay_thread_bytes'])
    for units in (4096,65536):
        rows=[t for t in profile['trajectories'] if t['units']==units]
        assert rows[0]['chain'][1:]==rows[1]['chain'][1:]
        variants=[c for c in profile['isolatedCodecs'] if c['units']==units]
        assert variants[0]['deltaSha256']==variants[1]['deltaSha256']
        for t in rows:
            e=t['encoding']
            assert e['firstEncoded']==1 and e['hotBaseEncodes']==0
            if t['external']: assert e['registeredDeltaPrepares']==16
            c=next(x for x in variants if x['external']==t['external'])
            thread_bytes=statistics.median(sum(a['AllocatedBytes'] for a in group) for group in zip(t['readCosts'],t['editCosts'],t['saveCosts']))
            w.writerow([units,'registered' if t['external'] else 'builtin',e['firstEncoded'],e['hotBaseEncodes'],c['baseBodyBytes'],t['chain'][0]['BodyBytes'],
                        sum(r['Kind']=='Base' for r in t['chain']),sum(r['Kind']=='Delta' for r in t['chain']),sum(r['BodyBytes'] for r in t['chain'][1:]),
                        e['firstFileBytes'],e['hotFileGrowth'],thread_bytes,c['deltaLength'],c['baseAllocated'],c['deltaAllocated'],c['replayAllocated']])
