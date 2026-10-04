#!/usr/bin/env python3
"""Produce a self-contained .NET 10 + pinned llama.cpp portable bundle, optionally with Qwen3 weights."""
import argparse, hashlib, json, os, pathlib, shutil, subprocess, tarfile, urllib.request, zipfile
ROOT = pathlib.Path(__file__).resolve().parent.parent

def fetch(asset, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        with target.open('rb') as f:
            if hashlib.file_digest(f, 'sha256').hexdigest() == asset['sha256']: return
    print('Downloading', target.name, flush=True)
    temp = target.with_suffix(target.suffix + '.part')
    try:
        urllib.request.urlretrieve(asset['url'], temp)
        with temp.open('rb') as f:
            assert hashlib.file_digest(f, 'sha256').hexdigest() == asset['sha256'], 'SHA-256 mismatch'
        assert temp.stat().st_size == asset['size'], 'Size mismatch'
        temp.replace(target)
    finally: temp.unlink(missing_ok=True)

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--rid', required=True); parser.add_argument('--version', default='0.1.0'); parser.add_argument('--with-model', action='store_true'); args = parser.parse_args()
    catalog = json.loads((ROOT/'config/catalog.json').read_text()); asset = catalog['engines'][args.rid]
    out = ROOT/'artifacts'/('publish-'+args.rid)
    if out.exists(): shutil.rmtree(out)
    subprocess.run(['dotnet','publish',str(ROOT/'src/TimsahOffice.App'),'-c','Release','-r',args.rid,'--self-contained','true','-p:PublishSingleFile=false','-o',str(out)], check=True, cwd=ROOT)
    engine_archive = ROOT/'artifacts/cache'/asset['url'].rsplit('/',1)[-1]; fetch(asset, engine_archive)
    engine = out/'engine'; engine.mkdir()
    if engine_archive.suffix == '.zip':
        with zipfile.ZipFile(engine_archive) as archive: archive.extractall(engine)
    else:
        with tarfile.open(engine_archive) as archive: archive.extractall(engine, filter='data')
    # Keep sibling shared libraries and backends required by llama-server; remove unrelated executable tools.
    for path in engine.rglob('llama-*'):
        if path.is_file() and not path.is_symlink() and path.stem != 'llama-server' and path.suffix not in ['.so','.dll','.dylib'] and '.so.' not in path.name and path.name != 'LICENSE': path.unlink()
    manifest = {'app':'Timsah-Office','version':args.version,'runtime':'.NET 10 (self-contained)','rid':args.rid,'llamaCpp':catalog['engineVersion'],'engineArchiveSha256':asset['sha256'],'models':[]}
    if args.with_model:
        model = catalog['models'][0]; source = ROOT/'artifacts/models'/model['fileName']; fetch(model, source)
        (out/'models').mkdir(); shutil.copy2(source,out/'models'/model['fileName']); manifest['models'].append({'id':model['id'],'sha256':model['sha256'],'size':model['size']})
    for name in ['README.md','LICENSE','THIRD_PARTY_NOTICES.md']: shutil.copy2(ROOT/name,out/name)
    shutil.copytree(ROOT/'licenses',out/'licenses')
    (out/'bundle-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    if args.rid.startswith('win'):
        (out/'Start-Timsah-Office.cmd').write_text('@echo off\r\ncd /d "%~dp0"\r\n"%~dp0Timsah-Office.exe" %*\r\n',encoding='ascii')
    else:
        launcher = out/('Start-Timsah-Office.command' if args.rid.startswith('osx') else 'Start-Timsah-Office.sh')
        launcher.write_text('#!/bin/sh\ncd "$(dirname "$0")"\nexec ./Timsah-Office "$@"\n'); launcher.chmod(0o755)
        (out/'Timsah-Office').chmod(0o755)
    label = f'Timsah-Office-{args.version}-{args.rid}-'+('offline' if args.with_model else 'lite')
    dest = ROOT/'artifacts'/label
    if args.rid.startswith('win'):
        archive_path = pathlib.Path(shutil.make_archive(str(dest),'zip',root_dir=out))
    else:
        archive_path = pathlib.Path(str(dest)+'.tar.gz')
        with tarfile.open(archive_path,'w:gz',compresslevel=6) as tar: tar.add(out,arcname=label)
    with archive_path.open('rb') as f: digest = hashlib.file_digest(f,'sha256').hexdigest()
    pathlib.Path(str(archive_path)+'.sha256').write_text(digest+'  '+archive_path.name+'\n')
    print(archive_path,flush=True)
if __name__=='__main__': main()
