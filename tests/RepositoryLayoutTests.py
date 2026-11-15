"""Check explicit project inputs and shared-source ownership; never runs the app."""
from pathlib import Path
import collections
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
projects = sorted((root / 'src').glob('*/*.csproj'))
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
        if kind == 'Compile': owners[path] += 1
        inputs += 1
shared = set((root / 'src/Shared').glob('*.cs'))
assert shared == {p for p, count in owners.items() if count > 1}
assert not list(root.glob('*.cs')) and not list(root.glob('*.csproj'))
assert all('SettingsDialog.cs' != p.name for p in owners)
print('PASS: 3 projects, %d declared inputs, %d shared sources; no test/build files compiled.' % (inputs, len(shared)))
