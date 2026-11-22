"""Replace four stale lobby lantern visuals with the existing Meshy WallTorch.

Dry run by default; --apply changes only four PrefabInstances, their stripped root
Transform source links, and four existing Flame local positions. All IDs, lights,
behaviours, camera, physics, child lists and SceneRoots are preserved.
"""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path('C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff')
SCENE = Path('Assets/Emberdelve/Scenes/Lobby.unity')
SOURCE = Path('Assets/Emberdelve/Art/Models/AI/Meshy/MapDressing/WallTorch/WallTorch.prefab')
OLD_GUID = b'cd09da8414db3dd43a8ed9d3dc61a166'
MODEL_POSITION = (0.0, -0.23, 0.045)  # MeshyMapDressing.TryBuildSconce


def documents(data):
    return {m.group(2): (m.group(1), m.group(0), m.start(), m.end()) for m in re.finditer(
        rb'^--- !u!(\d+) &(-?\d+)[^\n]*\n.*?(?=^--- !u!|\Z)', data, re.M | re.S)}


def ref(body, key):
    return re.search(rb'^  ' + key + rb': \{fileID: (-?\d+)\}', body, re.M).group(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    assert Path.cwd().resolve() == ROOT.resolve(), 'Run from the primary checkout.'
    assert Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip()).resolve() == ROOT.resolve()
    before = SCENE.read_bytes()
    old = documents(before)
    src = documents(SOURCE.read_bytes())
    assert all(c in (b'1', b'4', b'23', b'33') for c, _, _, _ in src.values()), 'Expected visual-only source.'
    guid = re.search(rb'^guid: (\w+)', SOURCE.with_suffix('.prefab.meta').read_bytes(), re.M).group(1)
    roots = [(k, d) for k, (c, d, _, _) in src.items() if c == b'4' and b'm_Father: {fileID: 0}' in d]
    assert len(roots) == 1
    root_transform, root_doc = roots[0]
    root_go = ref(root_doc, b'm_GameObject')
    socket_go = [k for k, (c, d, _, _) in src.items() if c == b'1' and b'm_Name: Flame_Socket' in d]
    assert len(socket_go) == 1
    socket = [d for c, d, _, _ in src.values() if c == b'4' and b'm_GameObject: {fileID: ' + socket_go[0] + b'}' in d][0]
    assert ref(socket, b'm_Father') == root_transform
    xyz = re.search(rb'm_LocalPosition: \{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}', socket).groups()
    flame_position = tuple(float(v) + offset for v, offset in zip(xyz, MODEL_POSITION))
    renderers = [k for k, (c, _, _, _) in src.items() if c == b'23']
    stale = [(k, v) for k, v in old.items() if v[0] == b'1001' and b'm_SourcePrefab: {fileID: 100100000, guid: ' + OLD_GUID in v[1]]
    if not stale:
        assert sum(c == b'1001' and b'm_SourcePrefab: {fileID: 100100000, guid: ' + guid in d for c, d, _, _ in old.values()) == 4
        print(json.dumps({'status': 'already migrated', 'guid': guid.decode()}))
        return
    assert len(stale) == 4, 'Expected precisely four saved legacy lobby lanterns.'
    replacements = {}
    for instance_id, (_, body, _, _) in stale:
        assert b'value: Torch_Model' in body
        parent = re.search(rb'm_TransformParent: \{fileID: (\d+)\}', body).group(1)
        parent_go = ref(old[parent][1], b'm_GameObject')
        assert b'm_Name: Sconce' in old[parent_go][1]
        torch = ref(old[parent][1], b'm_Father')
        assert b'm_Name: Lobby_Torch' in old[ref(old[torch][1], b'm_GameObject')][1]
        stripped = [(k, d) for k, (c, d, _, _) in old.items() if c == b'4' and b'm_PrefabInstance: {fileID: ' + instance_id + b'}' in d]
        assert len(stripped) == 1
        stripped_id, stripped_body = stripped[0]
        nl = b'\r\n' if b'\r\n' in body else b'\n'
        g, iid, par = guid.decode(), instance_id.decode(), parent.decode()
        overrides = []
        def override(target, prop, value):
            overrides.extend([f'    - target: {{fileID: {target.decode()}, guid: {g}, type: 3}}',
                              f'      propertyPath: {prop}', f'      value: {value}', '      objectReference: {fileID: 0}'])
        override(root_go, 'm_Name', 'Torch_Model')
        for axis, value in zip('xyz', MODEL_POSITION): override(root_transform, 'm_LocalPosition.' + axis, format(value, '.9g'))
        for axis in 'xyz': override(root_transform, 'm_LocalScale.' + axis, '1')
        for axis, value in zip('xyzw', (0, 0, 0, 1)): override(root_transform, 'm_LocalRotation.' + axis, value)
        for renderer in renderers: override(renderer, 'm_CastShadows', '0')
        lines = [f'--- !u!1001 &{iid}', 'PrefabInstance:', '  m_ObjectHideFlags: 0', '  serializedVersion: 2',
                 '  m_Modification:', '    serializedVersion: 3', f'    m_TransformParent: {{fileID: {par}}}',
                 '    m_Modifications:'] + overrides + ['    m_RemovedComponents: []', '    m_RemovedGameObjects: []',
                 '    m_AddedGameObjects: []', '    m_AddedComponents: []', f'  m_SourcePrefab: {{fileID: 100100000, guid: {g}, type: 3}}']
        replacements[instance_id] = nl.join(x.encode() for x in lines) + nl
        replacements[stripped_id] = re.sub(rb'm_CorrespondingSourceObject: \{fileID: -?\d+, guid: \w+, type: 3\}',
            b'm_CorrespondingSourceObject: {fileID: ' + root_transform + b', guid: ' + guid + b', type: 3}', stripped_body, count=1)
        flames = [(k, d) for k, (c, d, _, _) in old.items() if c == b'4' and b'm_Father: {fileID: ' + parent + b'}' in d
                  and b'm_Name: Flame' + nl in old[ref(d, b'm_GameObject')][1]]
        assert len(flames) == 1
        flame_id, flame = flames[0]
        position = '  m_LocalPosition: {x: %.9g, y: %.9g, z: %.9g}' % flame_position
        replacements[flame_id] = re.sub(rb'^  m_LocalPosition: .*$', position.encode() + (b'\r' if nl == b'\r\n' else b''), flame, count=1, flags=re.M)
    assert len(replacements) == 12
    after = before
    for k in sorted(replacements, key=lambda k: old[k][2], reverse=True):
        after = after[:old[k][2]] + replacements[k] + after[old[k][3]:]
    new = documents(after)
    assert set(new) == set(old), 'Object IDs must not change.'
    changed = [k for k in old if old[k][1] != new[k][1]]
    assert set(changed) == set(replacements)
    assert OLD_GUID not in after, 'Unexpected stale lantern reference outside the four visual instances.'
    preserved = {str(c.decode()): sum(cls == c for cls, _, _, _ in old.values()) for c in (b'108', b'114', b'20', b'65', b'215')}
    result = {'status': 'applied' if args.apply else 'dry run', 'newSourceGUID': guid.decode(),
              'flameLocalPosition': flame_position, 'changedDocuments': [k.decode() for k in changed],
              'changedClassCounts': {c: sum(new[k][0].decode() == c for k in changed) for c in ('1001', '4')},
              'preservedComponentCounts': preserved, 'beforeSHA256': hashlib.sha256(before).hexdigest(),
              'afterSHA256': hashlib.sha256(after).hexdigest(), 'unchangedDocuments': len(old) - len(changed)}
    if args.apply:
        evidence = Path('Logs/acceptance/lobby-meshy-braziers-20261122')
        evidence.mkdir(parents=True, exist_ok=True)
        backup = evidence / ('Lobby.before-walltorches-' + result['beforeSHA256'][:12] + '.unity')
        if not backup.exists(): backup.write_bytes(before)
        assert SCENE.read_bytes() == before, 'Scene changed during preparation.'
        SCENE.write_bytes(after)
        (evidence / 'walltorch-migration.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
