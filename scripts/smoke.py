#!/usr/bin/env python3
"""Headless live checks against a running Timsah-Office. Uses real llama.cpp when --inference is given."""
import argparse,json,pathlib,time,urllib.request,urllib.error
parser=argparse.ArgumentParser();parser.add_argument('--url',default='http://127.0.0.1:5273');parser.add_argument('--inference',action='store_true');parser.add_argument('--output',default='artifacts/smoke-results.json');args=parser.parse_args()
base=args.url+'/api/';token=json.load(urllib.request.urlopen(base+'session'))['token']
def request(path,method='GET',data=None,headers=None):
    req=urllib.request.Request(base+path,method=method,data=json.dumps(data).encode() if data is not None else None,headers={'X-Office-Token':token,'Content-Type':'application/json',**(headers or {})})
    with urllib.request.urlopen(req,timeout=180) as r:
        raw=r.read();return json.loads(raw) if raw else None
status=request('status')
if args.inference and status['engine']['state'] not in ['ready','unloaded']:
    if status['job']['state']!='running':request('engine/start','POST')
    for attempt in range(180):
        status=request('status')
        if status['engine']['state']=='ready': break
        if status['job']['state']=='error': raise RuntimeError(status['job']['error'])
        time.sleep(1)
results={'engine':status['engine'],'sources':status['sources'],'checks':[]}
assert len(status['models'])==4
assert request('rules/search?q=5.6')[0]['section']['number']=='5.6';results['checks'].append('official rules retrieval')
page=request('notes','POST',{'parentId':None});page=request('notes/'+page['id'],'PUT',{'title':'スモーク検証','markdown':'- [ ] 機体寸法を測る\n- [ ] マガジンを点検する\n- [ ] 補給の練習をする','parentId':None,'favorite':False,'revision':page['revision']})
try:request('notes/'+page['id'],'PUT',{'title':'conflict','markdown':'','parentId':None,'favorite':False,'revision':1});raise AssertionError('Conflict was accepted')
except urllib.error.HTTPError as e:assert e.code==409
assert request('notes/'+page['id'])['markdown']==page['markdown'];results['checks'].append('notes persistence and revision conflict')
for headers in [{'Origin':'https://evil.example'},{'X-Office-Token':'wrong'},{'Host':'evil.example'}]:
    try:request('status',headers=headers);raise AssertionError('Untrusted request was accepted')
    except urllib.error.HTTPError as e:assert e.code==403
results['checks'].append('loopback origin host and API-token checks')
if args.inference:
    for label,data in [('identity',{'message':'あなたの名前を一文で教えて。','mode':'chat'}),('rules',{'message':'CoRE-2でスキルのクールダウンは何秒？ピットインとサプライの例外も説明して。','mode':'rules'}),('todos',{'message':'このメモからTODOを抽出してください。','mode':'todos','noteId':page['id']})]:
        events=[];start=time.monotonic();req=urllib.request.Request(base+'chat',method='POST',data=json.dumps(data).encode(),headers={'X-Office-Token':token,'Content-Type':'application/json'})
        with urllib.request.urlopen(req,timeout=900) as response:
            event=None
            for line in response:
                text=line.decode().strip()
                if text.startswith('event: '):event=text[7:]
                if text.startswith('data: '):events.append({'type':event,'data':json.loads(text[6:])})
        assert not any(e['type']=='error' for e in events),events
        assert any(e['type']=='done' for e in events),events
        answer=''.join(e['data'] for e in events if e['type']=='delta');assert answer
        if label=='identity':assert 'Timsah-Assitant' in answer,answer
        if label=='rules':assert any('5.6' in source['title'] for e in events if e['type']=='sources' for source in e['data']),events
        if label=='todos':assert all(task in answer for task in ['機体寸法を測る','マガジンを点検する','補給の練習をする']),answer
        results[label]={'elapsedSeconds':round(time.monotonic()-start,2),'answer':answer,'events':events}
        print(label,round(time.monotonic()-start,2),answer,flush=True)
    results['checks'].append('real inference identity rules and note assistance')
request('notes/'+page['id']+'?revision='+str(page['revision']),'DELETE')
out=pathlib.Path(args.output);out.parent.mkdir(parents=True,exist_ok=True);out.write_text(json.dumps(results,ensure_ascii=False,indent=2));print('PASSED',results['checks'],flush=True)
