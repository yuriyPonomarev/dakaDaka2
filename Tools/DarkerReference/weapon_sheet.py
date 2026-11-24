"""One-screen combat sheet of a Dark and Darker weapon from decoded JSON (zen_decode.py output).

usage: python weapon_sheet.py <decoded root> <WeaponName> [<WeaponName> ...]
       (WeaponName as in the game data: ArmingSword, Longsword, RecurveBow, ...)

Prints, per weapon: montage events that define the attack phases (collision on/off, combo
transition, combo-window close, input enables, guard ready, projectile release), the melee attack
rows (damage ratio, hit-stop, stuck), hit effects (weapon damage ratio, impact power), the
weapon's own impact resistance and its movement modifiers. Seconds at play rate 1.
"""
import json
import os
import sys

EVENTS = {
    'Event.Attack.Collision.On.Stuck.Primary': 'stuckOn',
    'Event.Attack.Collision.On.Stuck.Secondary': 'stuckOn',
    'Event.Attack.Transition': 'transition',
    'Event.Attack.ComboWindow.Close': 'comboClose',
    'Event.Input.PrimaryAttack.Enable': 'primaryEnable',
    'Event.Input.SecondaryAttack.Enable': 'secondaryEnable',
    'Event.Item.ImpactGuard.Ready': 'guardReady',
    'Event.Attack.FireProjectile': 'fire',
    'Event.Special.Reload': 'reload',
}


def tag(v):
    return v.get('TagName') if isinstance(v, dict) else v


def load(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def montage_line(path):
    d = load(path)
    exports = {e['name']: e for e in d['exports']}
    out = []
    for e in d['exports']:
        if (e.get('class') or {}).get('script') != 'AnimMontage':
            continue
        p = e.get('properties') or {}
        segs = []
        for t in p.get('SlotAnimTracks', [])[:1]:
            for s in (t.get('AnimTrack') or {}).get('AnimSegments', []):
                rate = s.get('AnimPlayRate') or 1.0
                segs.append((s.get('StartPos', 0), (s.get('AnimEndTime', 0) - s.get('AnimStartTime', 0)) / rate, rate))
        length = max([a + b for a, b, _ in segs] + [0])
        ev = []
        for n in p.get('Notifies', []):
            obj = n.get('Notify') or n.get('NotifyStateClass') or {}
            ox = exports.get(obj.get('export')) if isinstance(obj, dict) else None
            props = (ox or {}).get('properties') or {}
            t = n.get('LinkValue', 0)
            if 'BeginEventTag' in props and tag(props['BeginEventTag']) == 'Event.Attack.Collision.On':
                ev.append((t, 'hit %.3f-%.3f' % (t, t + n.get('Duration', 0))))
            elif 'EventTag' in props:
                name = tag(props['EventTag'])
                if name in EVENTS:
                    ev.append((t, '%s %.3f' % (EVENTS[name], t)))
                elif name and name.startswith('Event.Attack.SetNextAttack'):
                    ev.append((t, 'next=%s' % name.split('.')[-1]))
            elif 'SectionName' in props:
                ev.append((t, 'jump:%s %.3f' % (props['SectionName'], t)))
        secs = ' '.join('%s@%.2f' % (s.get('SectionName'), s.get('LinkValue', 0)) for s in p.get('CompositeSections', []))
        segtxt = ' '.join('%.3f%s' % (b, '' if r == 1.0 else '(x%s)' % r) for a, b, r in segs)
        out.append('%-36s len %.3f in %s out %s | %s | sec[%s] seg[%s]' % (
            e['name'], length, (p.get('BlendIn') or {}).get('BlendTime'), (p.get('BlendOut') or {}).get('BlendTime'),
            ', '.join(x for _, x in sorted(ev)), secs, segtxt))
    return out


def rows(v2, sub, prefix, fields=None, skip=()):
    base = os.path.join(v2, *sub.split('/'))
    out = []
    if not os.path.isdir(base):
        return out
    for f in sorted(os.listdir(base)):
        if not f.startswith(prefix) or not f.endswith('.json'):
            continue
        for e in load(os.path.join(base, f))['exports']:
            p = e.get('properties') or {}
            vals = []
            for k, v in p.items():
                if k in ('V1Data', 'Name', 'Desc', 'FlavorText') or k in skip or (fields and k not in fields):
                    continue
                if isinstance(v, dict) and 'TagName' in v:
                    v = v['TagName']
                if isinstance(v, str) and v.startswith('/Game/'):
                    v = v.split('.')[-1]
                if isinstance(v, list):
                    v = [(x.get('TagName') if isinstance(x, dict) and 'TagName' in x else
                          (x.split('.')[-1] if isinstance(x, str) else x)) for x in v]
                if isinstance(v, dict) and 'package' in v:
                    v = str(v['package']).split('/')[-1]
                vals.append('%s=%s' % (k, v))
            out.append('%-52s %s' % (f[:-5], ' '.join(vals)))
    return out


def main():
    root = sys.argv[1]
    content = None
    for c in ('pakchunk500-Windows',):
        content = os.path.join(root, c, 'DungeonCrawler', 'Content', 'DungeonCrawler')
    v2 = os.path.join(content, 'Data', 'Generated', 'V2')
    for w in sys.argv[2:]:
        print('=' * 20, w)
        wdir = os.path.join(content, 'Item', 'Weapon', w)
        if not os.path.isdir(wdir):
            wdir = os.path.join(content, 'Item', 'Shield', w)
        for dp, dn, fn in os.walk(wdir):
            for f in sorted(fn):
                if f.startswith('AM_') and f.endswith('.json') and 'Equip' not in f and '_WP_' not in f:
                    for line in montage_line(os.path.join(dp, f)):
                        print('  ' + line)
        stuck = ('WeakShieldStuckPlayRateDuration', 'WeakShieldStuckBlendOutTime', 'MidShieldStuckPlayRateDuration',
                 'HeavyShieldStuckPlayRateDuration', 'CanStuckByStaticObject', 'StaticObjectStuckPlayRate',
                 'StaticObjectStuckPlayRateDuration', 'StaticObjectStuckBlendOutTime')
        for line in rows(v2, 'MeleeAttack/MeleeAttack', 'Id_MeleeAttack_GA_%s' % w, skip=stuck):
            print('  ' + line)
        for line in rows(v2, 'Item/ItemEffect', 'Id_ItemEffect_%s' % w, skip=('EffectClass', 'TargetType', 'AssetTags')):
            print('  ' + line)
        for line in rows(v2, 'MovementModifier/MovementModifier', 'Id_MovementModifier_%s' % w,
                         fields=('ExactEventTag', 'Multiply', 'Add')):
            print('  ' + line)
        for line in rows(v2, 'ItemProperty/ItemProperty', 'Id_ItemProperty_Primary_%s_3001' % w):
            print('  ' + line)
        for line in rows(v2, 'Item/Item', 'Id_Item_%s_3001' % w, fields=('HandType', 'WeaponTypes', 'MaxAmmoCount', 'Abilities')):
            print('  ' + line)


if __name__ == '__main__':
    main()
