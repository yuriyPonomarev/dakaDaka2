"""Print the notify timeline of decoded AnimMontage JSON files (output of zen_decode.py).

usage: python montage_summary.py <decoded dir or file> [name-substring ...]

For every montage: play length, sections, blend times and each notify with its trigger time,
duration and the payload of the notify object (event tags, flags). Times are seconds at
montage play rate 1.0; the game scales them by action speed at run time.
"""
import json
import os
import sys


def brief(v):
    if isinstance(v, dict):
        if set(v.keys()) == {'TagName'}:
            return v['TagName']
        if 'export' in v and len(v) == 1:
            return '@' + str(v['export'])
        if 'package' in v:
            return str(v['package']).split('/')[-1]
        if 'script' in v:
            return v['script']
        return {k: brief(x) for k, x in v.items() if x not in (None, [], 0, False, '')}
    if isinstance(v, list):
        return [brief(x) for x in v]
    return v


def summarize(path):
    with open(path, encoding='utf-8') as f:
        d = json.load(f)
    exports = {e['name']: e for e in d['exports']}
    for e in d['exports']:
        if (e.get('class') or {}).get('script') != 'AnimMontage':
            continue
        p = e.get('properties') or {}
        length = max([s.get('SegmentLength', 0) for s in p.get('CompositeSections', [])] + [0])
        segs = []
        for t in p.get('SlotAnimTracks', []):
            for s in (t.get('AnimTrack') or {}).get('AnimSegments', []):
                segs.append('%s:%s[%.3f..%.3f]x%s' % (t.get('SlotName'), brief(s.get('AnimReference')),
                                                      s.get('AnimStartTime', 0), s.get('AnimEndTime', 0),
                                                      s.get('AnimPlayRate')))
                length = max(length, s.get('StartPos', 0) + (s.get('AnimEndTime', 0) - s.get('AnimStartTime', 0)))
        print('%s  length %.3f  blendIn %s blendOut %s  rate %s%s' % (
            e['name'], length, (p.get('BlendIn') or {}).get('BlendTime'), (p.get('BlendOut') or {}).get('BlendTime'),
            p.get('RateScale', 1.0), '  ERROR ' + e['error'] if 'error' in e else ''))
        secs = ['%s@%.3f->%s' % (s.get('SectionName'), s.get('LinkValue', 0), s.get('NextSectionName'))
                for s in p.get('CompositeSections', [])]
        print('    sections: %s' % ', '.join(secs))
        print('    segments: %s' % ', '.join(segs))
        rows = []
        for n in p.get('Notifies', []):
            t = n.get('LinkValue', 0)
            dur = n.get('Duration', 0)
            obj = n.get('Notify') or n.get('NotifyStateClass') or {}
            payload = ''
            oname = obj.get('export') if isinstance(obj, dict) else None
            if oname in exports:
                ox = exports[oname]
                cls = (ox.get('class') or {}).get('script') or 'bp-class'
                props = ox.get('properties')
                payload = '%s %s' % (cls, json.dumps(brief(props), ensure_ascii=False) if props else
                                     ('(%s)' % ox['error'][:40] if 'error' in ox else ''))
            rows.append((t, dur, n.get('NotifyName'), payload, n.get('MontageTickType')))
        for t, dur, name, payload, tick in sorted(rows, key=lambda r: r[0]):
            span = '%.3f' % t if not dur else '%.3f..%.3f (%.3f)' % (t, t + dur, dur)
            print('    %-24s %-30s %s' % (span, name, payload))


def main():
    target = sys.argv[1]
    subs = [s.lower() for s in sys.argv[2:]]
    files = []
    if os.path.isfile(target):
        files = [target]
    else:
        for dp, dn, fn in os.walk(target):
            for f in sorted(fn):
                if f.endswith('.json') and f.startswith('AM_') and (not subs or any(s in f.lower() for s in subs)):
                    files.append(os.path.join(dp, f))
    for f in files:
        summarize(f)


if __name__ == '__main__':
    main()
