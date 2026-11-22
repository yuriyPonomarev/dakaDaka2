"""Insert the baked lobby brazier prefab as the final Lobby_Room child.

Dry run by default; --apply writes only Lobby.unity plus a backup/evidence in Logs.
No Unity invocation. Existing scene documents stay byte-identical except the parent child list.
"""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path('C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff')
SCENE = Path('Assets/Emberdelve/Scenes/Lobby.unity')
PREFAB = Path('Assets/Emberdelve/Prefabs/Environment/Lobby_Meshy_Braziers.prefab')


def documents(data):
    return {m.group(2): (m.group(1), m.group(0), m.start(), m.end()) for m in re.finditer(
        rb'^--- !u!(\d+) &(-?\d+)[^\n]*\n.*?(?=^--- !u!|\Z)', data, re.M | re.S)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    assert Path.cwd().resolve() == ROOT.resolve(), 'Run from the primary checkout.'
    actual = subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip()
    assert Path(actual).resolve() == ROOT.resolve(), 'Wrong Git root.'
    before = SCENE.read_bytes()
    source = PREFAB.read_bytes()
    guid = re.search(rb'^guid: (\w+)', PREFAB.with_suffix('.prefab.meta').read_bytes(), re.M).group(1)
    old = documents(before)
    src = documents(source)
    root_transforms = [(k, d) for k, (cls, d, _, _) in src.items() if cls == b'4' and b'm_Father: {fileID: 0}' in d]
    assert len(root_transforms) == 1, 'Expected one prefab root.'
    source_transform, transform_doc = root_transforms[0]
    source_go = re.search(rb'm_GameObject: \{fileID: (\d+)\}', transform_doc).group(1)
    assert b'm_Name: Lobby_Meshy_Braziers' in src[source_go][1]
    if re.search(rb'm_SourcePrefab: \{fileID: 100100000, guid: ' + guid, before):
        print(json.dumps({'status': 'already installed', 'guid': guid.decode()}))
        return
    gos = [k for k, (cls, d, _, _) in old.items() if cls == b'1' and re.search(rb'^  m_Name: Lobby_Room\r?$', d, re.M)]
    assert len(gos) == 1, 'Expected one Lobby_Room.'
    parents = [(k, val) for k, val in old.items() if val[0] == b'4' and b'm_GameObject: {fileID: ' + gos[0] + b'}' in val[1]]
    assert len(parents) == 1
    parent_id, (_, parent, start, end) = parents[0]
    nl = b'\r\n' if b'\r\n' in parent else b'\n'
    child_list = re.search(rb'^  m_Children:\r?\n(?:  - \{fileID: -?\d+\}\r?\n)*', parent, re.M)
    assert child_list, 'Expected nonempty Lobby_Room child list.'
    candidate = 710000000000000001
    while str(candidate).encode() in old or str(candidate + 1).encode() in old:
        candidate += 2
    instance_id, stripped_id = str(candidate).encode(), str(candidate + 1).encode()
    added_child = b'  - {fileID: ' + stripped_id + b'}' + nl
    new_parent = parent[:child_list.end()] + added_child + parent[child_list.end():]
    g, instance, stripped, root, source_t = (x.decode() for x in (guid, instance_id, stripped_id, parent_id, source_transform))
    text = f'''--- !u!1001 &{instance}
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {{fileID: {root}}}
    m_Modifications: []
    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {{fileID: 100100000, guid: {g}, type: 3}}
--- !u!4 &{stripped} stripped
Transform:
  m_CorrespondingSourceObject: {{fileID: {source_t}, guid: {g}, type: 3}}
  m_PrefabInstance: {{fileID: {instance}}}
  m_PrefabAsset: {{fileID: 0}}
'''.encode().replace(b'\n', nl)
    edited = before[:start] + new_parent + before[end:]
    roots = re.search(rb'^--- !u!1660057539 .*', edited, re.M)
    assert roots, 'Expected saved SceneRoots document.'
    after = edited[:roots.start()] + text + edited[roots.start():]
    new = documents(after)
    changed = [k.decode() for k in old if old[k][1] != new[k][1]]
    assert changed == [parent_id.decode()]
    assert len(new) == len(old) + 2
    assert new_parent.replace(added_child, b'', 1) == parent
    assert old[b'9223372036854775807'][1] == new[b'9223372036854775807'][1]
    result = {'status': 'applied' if args.apply else 'dry run', 'guid': g,
              'prefabRootFileID': source_go.decode(), 'prefabTransformFileID': source_t,
              'sceneParentFileID': root, 'instanceFileID': instance, 'strippedTransformFileID': stripped,
              'changedExistingDocuments': changed, 'unchangedExistingDocuments': len(old) - 1,
              'beforeSHA256': hashlib.sha256(before).hexdigest(), 'afterSHA256': hashlib.sha256(after).hexdigest()}
    if args.apply:
        evidence = Path('Logs/acceptance/lobby-meshy-braziers-20261122')
        evidence.mkdir(parents=True, exist_ok=True)
        backup = evidence / ('Lobby.before-' + result['beforeSHA256'][:12] + '.unity')
        if not backup.exists():
            backup.write_bytes(before)
        assert SCENE.read_bytes() == before, 'Lobby changed during preparation; retry.'
        SCENE.write_bytes(after)
        (evidence / 'scene-insertion.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
