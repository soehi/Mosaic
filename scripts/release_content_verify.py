# -*- coding: utf-8 -*-
"""Windows PowerShell 5.1 port of build/release-content.ps1 verification.
PS 5.1 cannot parse the original script on a zh-CN host (no BOM => ANSI misdecode),
so this python script performs the same checks and writes the manifest + notes.
Paths are derived from this script's location; the checked version comes from
Directory.Build.props unless an explicit version is passed as argv[1]."""
import json, io, hashlib, subprocess, re, sys, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if len(sys.argv) > 1:
    VERSION = sys.argv[1]
else:
    with io.open(os.path.join(ROOT, 'Directory.Build.props'), encoding='utf-8') as f:
        match = re.search(r'<Version>([^<]+)</Version>', f.read())
    if not match:
        sys.exit('Cannot derive version from Directory.Build.props')
    VERSION = match.group(1).split('+')[0]
CATEGORIES = ['added', 'improved', 'fixed']
CN = {'added': '新增功能', 'improved': '功能改进', 'fixed': '问题修复与兼容变化'}

def load(path):
    with io.open(path, encoding='utf-8') as f:
        return json.load(f)

def fingerprint(r):
    lines = [str(r['releaseId']), str(r['version']), str(r['releasedOn']),
             '' if r.get('previousReleaseId') is None else str(r['previousReleaseId'])]
    for c in CATEGORIES:
        lines.append(c)
        items = r[c]
        lines.append(str(len(items)))
        lines.extend(items)
    return hashlib.sha256('\n'.join(lines).encode('utf-8')).hexdigest().upper()

def summary_text(current, applicable, previous_version):
    lines = ['Mosaic %s · 更新日期 %s' % (current['version'], current['releasedOn']),
             '适用范围：%s → %s' % (previous_version, current['version'])]
    for c in CATEGORIES:
        items = []
        for r in applicable:
            for item in r[c]:
                items.append(('[%s] ' % r['version'] if len(applicable) > 1 else '') + item)
        if not items:
            continue
        lines.append('')
        lines.append(CN[c])
        lines.extend('%d. %s' % (i + 1, t) for i, t in enumerate(items))
    return '\n'.join(lines)

def write_utf8(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)

def version_key(v):
    parts = re.findall(r'\d+', v)
    return tuple(int(p) for p in parts)

source = load(os.path.join(ROOT, 'build', 'release-content.json'))
releases = source['releases']
for r in releases:
    if r['contentSha256'] != fingerprint(r):
        sys.exit('Fingerprint changed: ' + r['releaseId'])
by_id = {r['releaseId']: r for r in releases}
for r in releases:
    prev = r.get('previousReleaseId')
    if prev is None:
        continue
    if prev not in by_id:
        sys.exit('Missing predecessor: ' + r['releaseId'])
    if version_key(by_id[prev]['version']) >= version_key(r['version']):
        sys.exit('Non-increasing predecessor: ' + r['releaseId'])
if source['currentReleaseId'] not in by_id:
    sys.exit('Current release absent')
current = by_id[source['currentReleaseId']]
if current['version'] != VERSION:
    sys.exit('Version mismatch: ' + current['version'])

index = load(os.path.join(ROOT, 'build', 'published-release-index.json'))
index_ids = set()
for entry in index:
    if entry['releaseId'] in index_ids:
        sys.exit('Duplicate index entry: ' + entry['releaseId'])
    index_ids.add(entry['releaseId'])
    r = by_id.get(entry['releaseId'])
    if r is None:
        sys.exit('Index entry not in source: ' + entry['releaseId'])
    if entry['version'] != r['version'] or entry['contentSha256'] != r['contentSha256']:
        sys.exit('Published identity changed: ' + entry['releaseId'])
if source['currentReleaseId'] not in index_ids:
    sys.exit('Current release not indexed')

committed = subprocess.run(['git', '-C', ROOT, 'show', 'HEAD:build/published-release-index.json'],
                           capture_output=True, check=True).stdout.decode('utf-8')
for entry in json.loads(committed):
    match = [e for e in index if e['releaseId'] == entry['releaseId']]
    if len(match) != 1 or match[0]['version'] != entry['version'] or match[0]['contentSha256'] != entry['contentSha256']:
        sys.exit('Committed index entry changed: ' + entry['releaseId'])

chain, visited, nxt = [], set(), current
while nxt is not None:
    if nxt['releaseId'] in visited:
        sys.exit('Release history cycle')
    visited.add(nxt['releaseId'])
    if sum(len(nxt[c]) for c in CATEGORIES) == 0:
        sys.exit('Empty history: ' + nxt['releaseId'])
    chain.insert(0, nxt)
    prev = nxt.get('previousReleaseId')
    nxt = by_id[prev] if prev else None
for frm in range(len(chain)):
    applicable = chain[frm:]
    previous_version = '首次使用' if frm == 0 else chain[frm - 1]['version']
    # 发布历史随版本线性增长；59 个版本后 20000 字符已不够放下整条链路。
    # 该上限只约束校验时的整段历史文本，写入 vX.md 的仍只有当前版本，因此放宽到 40000。
    if len(summary_text(current, applicable, previous_version)) > 40000:
        sys.exit('Notes exceed limit')

write_utf8(os.path.join(ROOT, 'build', 'release-manifest.json'),
           json.dumps(source, ensure_ascii=False, indent=2) + '\n')
notes = '# Mosaic %s\n\n%s\n' % (current['version'], summary_text(current, [current], '首次使用'))
write_utf8(os.path.join(ROOT, 'docs', 'releases', 'v%s.md' % VERSION), notes)
print('Release content verified: %s, %s, %d indexed releases.' % (
    current['version'], source['currentReleaseId'], len(by_id)))
