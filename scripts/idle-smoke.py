#!/usr/bin/env python3
"""Linux acceptance check: set IdleUnloadMinutes=1 before running this against a ready model."""
import json,os,pathlib,time,urllib.request
base='http://127.0.0.1:5273/api/';token=json.load(urllib.request.urlopen(base+'session'))['token']
def get():return json.load(urllib.request.urlopen(urllib.request.Request(base+'status',headers={'X-Office-Token':token})))
status=get();assert status['settings']['idleUnloadMinutes']==1
pid=status['engine']['processId'];start=time.monotonic()
for attempt in range(45):
    status=get()
    if status['engine']['state']=='unloaded':break
    time.sleep(2)
assert status['engine']['state']=='unloaded',status
assert status['engine']['workingSetBytes']==0
if pid:
    try:os.kill(pid,0);raise AssertionError('Model process survived unload')
    except ProcessLookupError:pass
print('Unloaded; model process exited',round(time.monotonic()-start,2),flush=True)
req=urllib.request.Request(base+'chat',method='POST',headers={'X-Office-Token':token,'Content-Type':'application/json'},data=json.dumps({'message':'あなたの名前を一文で教えて。','mode':'chat'}).encode())
events=[];event=None
with urllib.request.urlopen(req,timeout=300) as r:
    for line in r:
        line=line.decode().strip()
        if line.startswith('event: '):event=line[7:]
        if line.startswith('data: '):events.append({'type':event,'data':json.loads(line[6:])})
assert events[0]['type']=='loading',events
assert events[-1]['type']=='done',events
status=get();assert status['engine']['state']=='ready';assert status['engine']['processId']!=pid
assert 'Timsah-Assitant' in ''.join(e['data'] for e in events if e['type']=='delta')
pathlib.Path('artifacts/idle-smoke.json').write_text(json.dumps({'oldPid':pid,'newPid':status['engine']['processId'],'memoryAfterReload':status['engine']['workingSetBytes'],'events':events},ensure_ascii=False,indent=2),encoding="utf-8")
print('PASSED automatic unload and on-demand reload',flush=True)
