# -*- coding: utf-8 -*-
"""Port of build/package.ps1 payload generation + WiX build for hosts that only
have Windows PowerShell 5.1 ([IO.Path]::GetRelativePath is .NET Core-only).

Generates artifacts/installer/Payload.wxs with byte-compatible ids (component
ids, directory ids and component GUIDs match the PowerShell generator), then
runs the WiX build. Assumes artifacts/local-run has already been published and
verified (verify-published + release_content_verify run by package.ps1 before
this fallback). The repository root is derived from this script's location.
"""
import hashlib, io, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PUBLISH = os.path.join(ROOT, 'artifacts', 'local-run')
PKGDIR = os.path.join(ROOT, 'artifacts', 'installer')
PAYLOAD = os.path.join(PKGDIR, 'Payload.wxs')
RUNTIME_DIRS = {'Data', 'Config', 'Logs'}
NS = 'http://wixtoolset.org/schemas/v4/wxs'
if len(sys.argv) > 1:
    VERSION = sys.argv[1]
else:
    import re
    with io.open(os.path.join(ROOT, 'Directory.Build.props'), encoding='utf-8') as f:
        match = re.search(r'<Version>([^<]+)</Version>', f.read())
    if not match:
        sys.exit('Cannot derive version from Directory.Build.props')
    VERSION = match.group(1).split('+')[0]
SKIP_VALIDATION = len(sys.argv) > 2 and sys.argv[2] == 'skip-validation'

def sha(path):
    return hashlib.sha256(path.lower().encode('utf-8')).hexdigest().upper()

def mosaic_id(path):
    return sha(path)[:24]

def guid_for(relative):
    digest = hashlib.sha256(('Mosaic.Installer/' + relative.lower()).encode('utf-8')).digest()[:16]
    b = list(digest)
    import struct
    d1, d2, d3 = struct.unpack('<IHH', bytes(b[:8]))
    d4 = ''.join('%02x' % c for c in b[8:16])
    return '{%08x-%04x-%04x-%s-%s}' % (d1, d2, d3, d4[:4], d4[4:])

def rel(path):
    return os.path.relpath(path, PUBLISH)

def top_segment(r):
    return r.replace('/', os.sep).split(os.sep)[0]

def include(name, r):
    if name == '.mosaic.instance.lock':
        return False
    return top_segment(r) not in RUNTIME_DIRS

def esc(s):
    return s.replace('&', '&amp;').replace('"', '&quot;').replace('<', '&lt;').replace('>', '&gt;')

# Collect directories and files exactly like Get-ChildItem -Recurse | Sort-Object FullName
# (Windows OrdinalIgnoreCase sort of the full path).
all_dirs, all_files = [], []
for dp, dns, fns in os.walk(PUBLISH):
    for d in dns:
        p = os.path.join(dp, d)
        r = rel(p)
        if include(d, r):
            all_dirs.append((p, r, d))
    for fn in fns:
        p = os.path.join(dp, fn)
        r = rel(p)
        if include(fn, r) and not fn.lower().endswith('.pdb'):
            all_files.append((p, r, fn))

sortkey = lambda t: t[0].lower()
all_dirs.sort(key=sortkey)
all_files.sort(key=sortkey)

# Build nested Directory elements like the PS script (parent must exist first).
directories = {'': 'INSTALLFOLDER'}
dir_elems = []
for p, r, name in all_dirs:
    parent = os.path.dirname(r)
    assert parent in directories, 'parent directory missing: ' + r
    rid = 'D' + mosaic_id(r)
    directories[r] = rid
    dir_elems.append((r, '<Directory Id="%s" Name="%s">' % (rid, esc(name))))

# Emit DirectoryRef with nested Directory tree (PS appends children in walk order;
# replicate by nesting under each directory's parent).
children = {'': []}
for r, elem in dir_elems:
    parent = os.path.dirname(r)
    children.setdefault(parent, []).append((r, elem))

def render(parent, depth):
    out = []
    for r, elem in children.get(parent, []):
        out.append('  ' * depth + elem)
        out.extend(render(r, depth + 1))
        out.append('  ' * depth + '</Directory>')
    return out

lines = ['<Wix xmlns="%s">' % NS, '  <Fragment>', '    <DirectoryRef Id="INSTALLFOLDER">']
lines.extend(render('', 3))
lines.append('    </DirectoryRef>')

components = []
for p, r, fn in all_files:
    fid = mosaic_id(r)
    d = os.path.dirname(r)
    dir_id = directories[d]
    g = guid_for(r)
    components.append(
        '<Component Id="C%s" Directory="%s" Guid="%s" Bitness="always64">'
        '<File Id="F%s" Source="%s" KeyPath="no" />'
        '<RegistryValue Root="HKCU" Key="Software\\Mosaic\\InstallerFiles" Name="%s" Type="integer" Value="1" KeyPath="yes" />'
        '</Component>' % (fid, dir_id, g, fid, esc(p), fid))

cleanup = ['<Component Id="DirectoryCleanup" Directory="INSTALLFOLDER" Guid="{B2050352-2EBE-4E24-A5FB-FC06A716DC98}" Bitness="always64">']
cleanup.append('<RemoveFolder Id="RemoveUserProgramsIfEmpty" Directory="UserPrograms" On="uninstall" />')
for dir_id in directories.values():
    cleanup.append('<RemoveFolder Id="R%s" Directory="%s" On="uninstall" />' % (mosaic_id(dir_id), dir_id))
cleanup.append('<RegistryValue Root="HKCU" Key="Software\\Mosaic\\Installer" Name="Directories" Type="integer" Value="1" KeyPath="yes" />')
cleanup.append('</Component>')

lines.append('    <ComponentGroup Id="Payload">')
for c in components + cleanup:
    lines.append('      ' + c)
lines.append('    </ComponentGroup>')
lines.append('  </Fragment>')
lines.append('</Wix>')

os.makedirs(PKGDIR, exist_ok=True)
with io.open(PAYLOAD, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines) + '\n')
print('wrote %s: %d files, %d directories' % (PAYLOAD, len(components), len(directories) - 1))

out = os.path.join(PKGDIR, VERSION) + os.sep
args = ['dotnet', 'build', 'build/installer/Mosaic.Installer.wixproj', '-c', 'Release', '-m:1',
        '-p:ProductVersion=' + VERSION, '-p:PayloadFile=' + PAYLOAD, '-p:OutputPath=' + out]
if SKIP_VALIDATION:
    args.append('-p:SuppressValidation=true')
print('+', ' '.join(args))
sys.exit(subprocess.call(args, cwd=ROOT))
