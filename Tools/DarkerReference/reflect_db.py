"""Build a property-schema database from a UE5 shipping executable by reading its static
reflection tables (UECodeGen_Private FClassParams / FStructParams / FEnumParams / FPropertyParams).

The executable is only read, never run. The result plays the role of a `.usmap` mappings file:
cooked packages store properties without names ("unversioned"), so their order and types must
come from the program that wrote them.

usage: python reflect_db.py <Game.exe> <out_schema.json>

Layout facts were verified against the packages themselves (see README.md):
- FPropertyParams: +0 NameUTF8, +16 PropertyFlags, +24 GenFlags(u32, low 6 bits = type),
  +48 ArrayDim(u16), +50 Offset(u16), +56 type-specific pointer (struct / enum / class function).
- FStructParams: +0 OuterFunc, +8 SuperFunc, +24 NameUTF8, +32 PropertyArray, +40 Num(u16), +42 Size(u16).
- FClassParams: +0 ClassNoRegisterFunc, +24 DependencySingletons, +40 PropertyArray,
  +56 bitfield (deps:4, functions:11, properties:11, interfaces:6).
- FEnumParams: +16 NameUTF8, +24 CppType, +32 enumerators {NameUTF8, int64}.
"""
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE  # noqa: E402
from cityhash import script_object_id  # noqa: E402

GEN = {0x00: 'Byte', 0x01: 'Int8', 0x02: 'Int16', 0x03: 'Int', 0x04: 'Int64', 0x05: 'UInt16', 0x06: 'UInt32',
       0x07: 'UInt64', 0x08: 'Int', 0x09: 'UInt32', 0x0A: 'Float', 0x0B: 'Double', 0x0C: 'Bool',
       0x0D: 'SoftClass', 0x0E: 'WeakObject', 0x0F: 'LazyObject', 0x10: 'SoftObject', 0x11: 'Class',
       0x12: 'Object', 0x13: 'Interface', 0x14: 'Name', 0x15: 'Str', 0x16: 'Array', 0x17: 'Map', 0x18: 'Set',
       0x19: 'Struct', 0x1A: 'Delegate', 0x1B: 'MulticastDelegate', 0x1C: 'MulticastDelegate',
       0x1D: 'Text', 0x1E: 'Enum', 0x1F: 'FieldPath', 0x20: 'Double', 0x21: 'Optional', 0x22: 'VValue',
       0x23: 'Utf8Str', 0x24: 'AnsiStr'}

CPF_TRANSIENT = 0x2000
CPF_EDITOR_ONLY = 0x0000000800000000


class Reflection:
    def __init__(self, exe):
        self.pe = PE(exe)
        self.structs = {}      # name -> {'super': name|None, 'props': [...]}
        self.enums = {}        # name -> {value: label}
        self.classes = {}      # name -> {'package', 'super', 'props', 'id'}
        self._fn_struct = {}
        self._fn_enum = {}
        self._class_by_va = {}

    # ---- raw property params ----------------------------------------------
    def prop(self, va):
        pe = self.pe
        if not va or pe.sec(va) is None:
            return None
        name = pe.cstr(pe.q(va))
        if name is None:
            return None
        gen = pe.u32(va + 24)
        if gen is None or gen > 0xFF or (gen & 0x3F) not in GEN:
            return None
        d = {'name': name, 'type': GEN[gen & 0x3F], 'flags': pe.q(va + 16), 'dim': pe.u16(va + 48) or 1}
        if d['type'] != 'Bool':
            d['tail'] = pe.q(va + 56)
        return d

    def prop_array(self, arr, n):
        """Read PropPointers; fold container inner / enum underlying entries into their owner.

        UHT emits inner properties immediately before their owner: Inner, Array;
        ValueProp, [Key_Underlying,] Key, Map; Underlying, Enum."""
        out = []
        for i in range(n):
            p = self.prop(self.pe.q(arr + 8 * i))
            if p is None:
                raise ValueError('bad property at %x[%d]' % (arr, i))
            t = p['type']
            if t == 'Enum':
                p['underlying'] = out.pop() if out else None
            elif t in ('Array', 'Set', 'Optional'):
                p['inner'] = out.pop() if out else None
            elif t == 'Map':
                p['key'] = out.pop() if out else None
                p['value'] = out.pop() if out else None
            out.append(p)
        return out

    # ---- Z_Construct function -> params --------------------------------------
    def lea_targets(self, fva, span=160):
        pe = self.pe
        o = pe.off(fva)
        if o is None:
            return []
        code = pe.mm[o:o + span]
        out = []
        for i in range(len(code) - 7):
            if code[i] in (0x48, 0x4C) and code[i + 1] == 0x8D and (code[i + 2] & 0xC7) == 0x05:
                out.append(fva + i + 7 + struct.unpack_from('<i', code, i + 3)[0])
        return out

    def struct_params_at(self, t):
        pe = self.pe
        if pe.sec(pe.q(t) or 0) != '.text':
            return None
        name = pe.cstr(pe.q(t + 24))
        if name is None:
            return None
        arr, n = pe.q(t + 32), pe.u16(t + 40)
        if n and (n > 600 or self.prop(pe.q(arr) if pe.sec(arr) else 0) is None):
            return None
        return {'name': name, 'arr': arr, 'n': n, 'super_fn': pe.q(t + 8)}

    def enum_params_at(self, t):
        pe = self.pe
        if pe.sec(pe.q(t) or 0) != '.text':
            return None
        name = pe.cstr(pe.q(t + 16))
        cpp = pe.cstr(pe.q(t + 24))
        ev = pe.q(t + 32)
        if name is None or cpp is None or pe.sec(ev) is None:
            return None
        vals = {}
        for i in range(1024):
            nm = pe.cstr(pe.q(ev + 16 * i))
            if nm is None:
                break
            if '::' not in nm and i > 0 and not nm.startswith(name[:3]):
                # plain (non-namespaced) enums have bare labels; stop when the next table begins
                if pe.q(ev + 16 * i + 8) > 0xFFFF and pe.q(ev + 16 * i + 8) < 0xFFFFFFFFFFFF0000:
                    break
            v = struct.unpack('<q', struct.pack('<Q', pe.q(ev + 16 * i + 8)))[0]
            if v in vals and i > 300:
                break
            vals.setdefault(v, nm.split('::')[-1])
            if nm.split('::')[-1].endswith('_MAX') or nm.endswith('MAX'):
                break
        return {'name': name, 'values': vals} if vals else None

    def struct_of_fn(self, fva):
        if fva in self._fn_struct:
            return self._fn_struct[fva]
        name = None
        self._fn_struct[fva] = None
        for t in self.lea_targets(fva):
            if self.pe.sec(t) in ('.data', '.rdata'):
                sp = self.struct_params_at(t)
                if sp:
                    name = self.add_struct(sp)
                    break
        self._fn_struct[fva] = name
        return name

    def enum_of_fn(self, fva):
        if fva in self._fn_enum:
            return self._fn_enum[fva]
        name = None
        for t in self.lea_targets(fva):
            if self.pe.sec(t) in ('.data', '.rdata'):
                ep = self.enum_params_at(t)
                if ep:
                    name = ep['name']
                    self.enums.setdefault(name, {str(k): v for k, v in ep['values'].items()})
                    break
        self._fn_enum[fva] = name
        return name

    # ---- description -------------------------------------------------------------
    def add_struct(self, sp):
        name = sp['name']
        if name in self.structs:
            return name
        entry = {'super': None, 'props': []}
        self.structs[name] = entry
        if sp['super_fn']:
            entry['super'] = self.struct_of_fn(sp['super_fn'])
        entry['props'] = [self.describe(p) for p in (self.prop_array(sp['arr'], sp['n']) if sp['n'] else [])]
        return name

    def describe(self, p):
        if p is None:
            return None
        d = {'n': p['name'], 't': p['type']}
        if p['dim'] != 1:
            d['d'] = p['dim']
        t = p['type']
        tail = p.get('tail')
        if t == 'Struct':
            d['s'] = self.struct_of_fn(tail) if tail else None
        elif t in ('Enum', 'Byte'):
            e = self.enum_of_fn(tail) if tail and self.pe.sec(tail) == '.text' else None
            if e:
                d['e'] = e
            if p.get('underlying'):
                d['u'] = p['underlying']['type']
        elif t in ('Array', 'Set', 'Optional'):
            d['i'] = self.describe(p.get('inner'))
        elif t == 'Map':
            d['k'] = self.describe(p.get('key'))
            d['v'] = self.describe(p.get('value'))
        return d

    # ---- classes -------------------------------------------------------------------
    def class_params_at(self, t):
        pe = self.pe
        f = pe.q(t)
        if pe.sec(f or 0) != '.text':
            return None
        bits = pe.u32(t + 56)
        ndeps, nprop = bits & 0xF, (bits >> 15) & 0x7FF
        arr, deps = pe.q(t + 40), pe.q(t + 24)
        if nprop and (pe.sec(arr) is None or self.prop(pe.q(arr)) is None):
            return None
        if ndeps and pe.sec(deps) is None:
            return None
        if not nprop and arr:
            return None
        cfg = pe.q(t + 8)
        if cfg and pe.cstr(cfg) is None:
            return None
        pkg = cname = None
        prev = None
        for tg in self.lea_targets(f, 260):
            w = pe.wstr(tg)
            if not w:
                continue
            if w.startswith('/Script/'):
                pkg, cname = w, prev
                break
            prev = w
        if not pkg or not cname:
            return None
        return {'va': t, 'name': cname, 'package': pkg, 'arr': arr, 'nprop': nprop,
                'deps': [pe.q(deps + 8 * i) for i in range(ndeps)]}

    def scan_classes(self):
        pe = self.pe
        text = [s for s in pe.sections if s[0] == '.text'][0]
        tlo, thi = text[1], text[1] + text[4]
        found = []
        for name, sva, vsize, rptr, rsize in pe.sections:
            if name not in ('.rdata', '.data'):
                continue
            n = rsize // 8
            view = memoryview(pe.mm)[rptr:rptr + n * 8].cast('Q')
            for i in range(n - 8):
                v = view[i]
                if tlo <= v < thi:
                    c = view[i + 2]          # CppClassInfo pointer must be an image address
                    if c and not (pe.base <= c < pe.base + 0x20000000):
                        continue
                    cp = self.class_params_at(sva + 8 * i)
                    if cp:
                        found.append(cp)
            view.release()
        for cp in found:
            self._class_by_va[cp['va']] = cp
        fn_to_class = {}
        for cp in found:
            entry = {'package': cp['package'], 'super': None,
                     'id': '%016x' % script_object_id(cp['package'] + '.' + cp['name']),
                     'props': [self.describe(p) for p in
                               (self.prop_array(cp['arr'], cp['nprop']) if cp['nprop'] else [])]}
            key = cp['name']
            if key in self.classes and self.classes[key]['package'] != cp['package']:
                key = cp['package'].split('/')[-1] + '.' + cp['name']
            self.classes[key] = entry
            cp['key'] = key
        # super: first dependency singleton that constructs a known class
        for cp in found:
            for d in cp['deps']:
                if d not in fn_to_class:
                    hit = None
                    for tg in self.lea_targets(d, 520):
                        if tg in self._class_by_va:
                            hit = self._class_by_va[tg]['key']
                            break
                    fn_to_class[d] = hit
                if fn_to_class[d]:
                    self.classes[cp['key']]['super'] = fn_to_class[d]
                    break
        return len(found)


def main():
    exe, out = sys.argv[1], sys.argv[2]
    r = Reflection(exe)
    n = r.scan_classes()
    db = {'source': os.path.basename(exe), 'size': os.path.getsize(exe),
          'classes': r.classes, 'structs': r.structs, 'enums': r.enums}
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(db, f, separators=(',', ':'))
    print('classes %d, structs %d, enums %d -> %s' % (n, len(r.structs), len(r.enums), out))


if __name__ == '__main__':
    main()
