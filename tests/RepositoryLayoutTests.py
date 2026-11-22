"""Check explicit project inputs and shared-source ownership; never runs the app."""
from pathlib import Path
import collections
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
layers = ('app_main', 'app_request', 'app_tools')
assert all((root / name).is_dir() for name in layers)
legacy = root / 'src'
assert not any(
    path.is_file() and {'bin', 'obj'}.isdisjoint(path.relative_to(legacy).parts)
    for path in legacy.rglob('*')
), 'Legacy source files still reside in src'
projects = sorted((root / 'app_main').glob('*/*.csproj'))
assert len(projects) == 3
owners = collections.Counter()
inputs = 0
for project in projects:
    for node in ET.parse(project).iter():
        kind = node.tag.rsplit('}', 1)[-1]
        if kind in ('Compile', 'None', 'Page', 'Resource', 'EmbeddedResource'):
            value = node.attrib['Include']
        elif kind in ('ApplicationIcon', 'ApplicationManifest'):
            value = node.text
        else:
            continue
        path = (project.parent / value.replace('\\', '/')).resolve()
        assert path.is_relative_to(root) and path.is_file(), (project.name, value)
        assert not any(x in path.relative_to(root).parts for x in ('tests', 'bin', 'obj', 'checks'))
        if kind == 'Compile':
            assert path.relative_to(root).parts[0] in layers, (project.name, value)
            owners[path] += 1
        inputs += 1
shared = {root / path for path in (
    'app_main/Shared/AssemblyInfo.cs',
    'app_main/Shared/HotkeyBinding.cs',
    'app_request/Settings/SettingsWire.cs',
    'app_tools/Windows/AudioSourceNames.cs',
    'app_tools/Windows/DisplayBackend.cs',
)}
assert shared == {p for p, count in owners.items() if count > 1}
owned_entries = {
    'app_request/DriverSetup/Program.cs',
    'app_request/ScreenOffTray/Program.cs',
    'app_request/Settings/Program.cs',
    'app_request/Settings/SettingsClient.cs',
    'app_request/Settings/SettingsPipeHost.cs',
    'app_request/ScreenOffTray/VerifiedLaunch.cs',
}
assert all(owners[root / path] == 1 for path in owned_entries)
assert not list(root.glob('*.cs')) and not list(root.glob('*.csproj'))
assert all('SettingsDialog.cs' != p.name for p in owners)
print('PASS: 3 application directories, 3 projects, %d declared inputs, %d shared sources.' % (inputs, len(shared)))
