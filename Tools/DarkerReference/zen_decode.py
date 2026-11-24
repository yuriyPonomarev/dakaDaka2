"""Decode extracted Zen (IoStore) packages (`.uheader` + `.uexp`) into JSON using the schema
database(s) built by reflect_db.py.

usage:
  python zen_decode.py <schema.json[;schema2.json...]> <out_dir> <extracted_root> <glob-relative-to-root> [...]
  python zen_decode.py <schema.json[;schema2.json...]> - <file.uheader>     (print one package to stdout)

Several schemas may be given (builds of the game before and after the build the packages were
cooked with). Each export is decoded with every schema variant in turn; a variant is accepted only
when it consumes the export's bytes exactly. The accepted variant is recorded as `schema`.

Every export is decoded with the class named by its import hash. Results are honest about
limits: an export whose class is not native (Blueprint class), or whose decoding does not
consume the property block, is reported with `error` / `tail` instead of guessed values.
"""
import glob
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

TAG = b'\xc1\x83\x2a\x9e'
ZERO4 = bytes(4)


class Reader:
    def __init__(self, data, pos=0, end=None):
        self.d = data
        self.p = pos
        self.end = len(data) if end is None else end

    def take(self, n):
        if self.p + n > self.end:
            raise EOFError('read past end (%d + %d > %d)' % (self.p, n, self.end))
        b = self.d[self.p:self.p + n]
        self.p += n
        return b

    def u8(self): return self.take(1)[0]
    def i8(self): return struct.unpack('<b', self.take(1))[0]
    def u16(self): return struct.unpack('<H', self.take(2))[0]
    def i16(self): return struct.unpack('<h', self.take(2))[0]
    def u32(self): return struct.unpack('<I', self.take(4))[0]
    def i32(self): return struct.unpack('<i', self.take(4))[0]
    def u64(self): return struct.unpack('<Q', self.take(8))[0]
    def i64(self): return struct.unpack('<q', self.take(8))[0]
    def f32(self): return round(struct.unpack('<f', self.take(4))[0], 6)
    def f64(self): return round(struct.unpack('<d', self.take(8))[0], 6)

    def fstring(self):
        n = self.i32()
        if n == 0:
            return ''
        if n < 0:
            if -n > 1 << 20:
                raise ValueError('bad string length %d' % n)
            return self.take(-n * 2).decode('utf-16-le').rstrip('\0')
        if n > 1 << 20:
            raise ValueError('bad string length %d' % n)
        return self.take(n).decode('utf-8', 'replace').rstrip('\0')


def read_name_batch(d, pos):
    """FNameBatch: count, string bytes, hash version, hashes, big-endian length headers, strings."""
    count, nbytes = struct.unpack_from('<II', d, pos)
    pos += 8
    if count == 0:
        return [], pos
    pos += 8 + 8 * count
    lens = []
    for i in range(count):
        h = struct.unpack_from('>H', d, pos + 2 * i)[0]
        lens.append(h)
    pos += 2 * count
    names = []
    for h in lens:
        if h & 0x8000:
            n = (h & 0x7FFF) * 2
            names.append(d[pos:pos + n].decode('utf-16-le'))
        else:
            n = h
            names.append(d[pos:pos + n].decode('latin1'))
        pos += n
    return names, pos


class Package:
    def __init__(self, header_path, headers_only=False):
        self.path = header_path
        with open(header_path, 'rb') as f:
            h = f.read()
        self.h = h
        (self.header_size,) = struct.unpack_from('<I', h, 4)
        self.name_idx, self.name_num, self.flags, self.cooked_header_size = struct.unpack_from('<IIII', h, 8)
        (self.pub_hash_off, self.import_off, self.export_off, self.bundle_off,
         self.dep_hdr_off, self.dep_ent_off, self.imp_pkg_off) = struct.unpack_from('<7i', h, 24)
        self.names, _ = read_name_batch(h, 0x3c)
        self.pub_hashes = [struct.unpack_from('<Q', h, o)[0] for o in range(self.pub_hash_off, self.import_off, 8)]
        self.imports = [struct.unpack_from('<Q', h, o)[0] for o in range(self.import_off, self.export_off, 8)]
        self.exports = []
        for o in range(self.export_off, self.bundle_off, 72):
            (soff, ssize, nidx, nnum, outer, cls, sup, tmpl, phash, oflags) = struct.unpack_from('<QQIIQQQQQI', h, o)
            self.exports.append({'offset': soff, 'size': ssize, 'name': self.name(nidx, nnum), 'outer': outer,
                                 'class': cls, 'super': sup, 'template': tmpl, 'hash': phash, 'flags': oflags})
        self.bundle = [struct.unpack_from('<II', h, o) for o in range(self.bundle_off, self.dep_hdr_off, 8)]
        self.imported_packages = []
        if self.imp_pkg_off and self.imp_pkg_off < self.header_size:
            try:
                self.imported_packages, _ = read_name_batch(h, self.imp_pkg_off)
            except Exception:
                self.imported_packages = []
        self.package_name = self.name(self.name_idx, self.name_num)
        self.x = b''
        if not headers_only:
            with open(os.path.splitext(header_path)[0] + '.uexp', 'rb') as f:
                self.x = f.read()

    def name(self, idx, num=0):
        i = idx & 0x3FFFFFFF
        s = self.names[i] if i < len(self.names) else '<name %d>' % idx
        return s if num == 0 else '%s_%d' % (s, num - 1)

    def export_ranges(self):
        """Byte range of every export inside the .uexp (the extractor wrote offsets relative to it)."""
        return {i: (e['offset'], e['offset'] + e['size']) for i, e in enumerate(self.exports)}


class Decoder:
    def __init__(self, schema_paths, root=None):
        self.root = root
        self._pkg_index = None
        self._parent_cache = {}
        dbs = []
        for sp in schema_paths.split(';'):
            with open(sp, 'r', encoding='utf-8') as f:
                db = json.load(f)
            db['label'] = os.path.splitext(os.path.basename(sp))[0]
            dbs.append(db)
        # Variants: each database as is, plus every database with the small shared base classes
        # taken from another one (the packages were cooked by a build between the two).
        self.variants = []
        for a in dbs:
            for b in dbs:
                if a is b:
                    continue
                classes = dict(a['classes'])
                for base in SHARED_BASES:
                    if base in b['classes']:
                        classes[base] = b['classes'][base]
                self.variants.append({'label': '%s+bases:%s' % (a['label'], b['label']), 'classes': classes,
                                      'structs': a['structs'], 'enums': a['enums']})
        for a in dbs:
            self.variants.append({'label': a['label'], 'classes': a['classes'], 'structs': a['structs'],
                                  'enums': a['enums']})
        self.by_id = {}
        for v in self.variants:
            for n, c in v['classes'].items():
                self.by_id.setdefault(int(c['id'], 16), n)
            v['flat'] = {}
        self.use(self.variants[0])

    def use(self, variant):
        self.variant = variant
        self.classes = variant['classes']
        self.structs = variant['structs']
        self.enums = variant['enums']
        self._flat = variant['flat']

    # ---- schema ------------------------------------------------------------
    def flat(self, kind, name):
        """Serialization order: own properties first, then the super chain (static arrays expanded)."""
        key = (kind, name)
        if key in self._flat:
            return self._flat[key]
        out = []
        table = self.classes if kind == 'c' else self.structs
        seen = set()
        cur = name
        while cur and cur in table and cur not in seen:
            seen.add(cur)
            for p in table[cur]['props']:
                dim = p.get('d', 1)
                if dim == 1:
                    out.append((p, None))
                else:
                    for k in range(dim):
                        out.append((p, k))
            cur = table[cur].get('super')
        self._flat[key] = out
        return out

    # ---- object references -------------------------------------------------
    def obj_index(self, pkg, v):
        """FPackageObjectIndex -> readable."""
        if v == 0xFFFFFFFFFFFFFFFF:
            return None
        typ = v >> 62
        if typ == 0:
            i = v & 0xFFFFFFFF
            return {'export': pkg.exports[i]['name']} if i < len(pkg.exports) else {'export': i}
        if typ == 1:
            n = self.by_id.get(v)
            return {'script': n} if n else {'script_id': '%016x' % v}
        pi, hi = (v >> 32) & 0x3FFFFFFF, v & 0xFFFFFFFF
        d = {'package': pkg.imported_packages[pi] if pi < len(pkg.imported_packages) else pi}
        if hi < len(pkg.pub_hashes):
            d['export_hash'] = '%016x' % pkg.pub_hashes[hi]
        return d

    def pkg_index(self, pkg, i):
        if i == 0:
            return None
        if i > 0:
            return {'export': pkg.exports[i - 1]['name']} if i - 1 < len(pkg.exports) else {'export': i - 1}
        j = -i - 1
        return self.obj_index(pkg, pkg.imports[j]) if j < len(pkg.imports) else {'import': j}

    # ---- unversioned properties -------------------------------------------------
    def read_props(self, pkg, r, kind, name):
        schema = self.flat(kind, name)
        frags = []
        zero_bits = 0
        while True:
            f = r.u16()
            skip, has_zero, last, num = f & 0x7F, bool(f & 0x80), bool(f & 0x100), f >> 9
            frags.append((skip, has_zero, num))
            if has_zero:
                zero_bits += num
            if last:
                break
        mask = 0
        if zero_bits:
            if zero_bits <= 8:
                mask = r.u8()
            elif zero_bits <= 16:
                mask = r.u16()
            else:
                words = (zero_bits + 31) // 32
                mask = 0
                for w in range(words):
                    mask |= r.u32() << (32 * w)
        out = {}
        idx = 0
        zi = 0
        for skip, has_zero, num in frags:
            idx += skip
            for _ in range(num):
                if idx >= len(schema):
                    raise IndexError('%s: property index %d outside schema (%d)' % (name, idx, len(schema)))
                p, elem = schema[idx]
                is_zero = False
                if has_zero:
                    is_zero = bool((mask >> zi) & 1)
                    zi += 1
                key = p['n'] if elem is None else '%s[%d]' % (p['n'], elem)
                try:
                    out[key] = self.zero(p) if is_zero else self.read_value(pkg, r, p)
                except Exception as e:
                    out['!error'] = '%s (%s): %s' % (key, p['t'], e)
                    raise DecodeError(out)
                idx += 1
        return out

    @staticmethod
    def zero(p):
        t = p['t']
        if t in ('Float', 'Double', 'Int', 'Int8', 'Int16', 'Int64', 'UInt16', 'UInt32', 'UInt64', 'Byte'):
            return 0
        if t == 'Bool':
            return False
        return None

    def enum_label(self, p, v):
        e = self.enums.get(p.get('e') or '')
        if e is not None:
            return e.get(str(v), v)
        return v

    def read_value(self, pkg, r, p):
        t = p['t']
        if t == 'Float':
            return r.f32()
        if t == 'Int':
            return r.i32()
        if t == 'Bool':
            return bool(r.u8())
        if t == 'Double':
            return r.f64()
        if t == 'Byte':
            return self.enum_label(p, r.u8())
        if t == 'Enum':
            u = p.get('u') or 'Byte'
            v = {'Byte': r.u8, 'Int8': r.i8, 'Int16': r.i16, 'UInt16': r.u16, 'Int': r.i32, 'UInt32': r.u32,
                 'Int64': r.i64, 'UInt64': r.u64}[u]()
            return self.enum_label(p, v)
        if t == 'Int8':
            return r.i8()
        if t == 'Int16':
            return r.i16()
        if t == 'Int64':
            return r.i64()
        if t == 'UInt16':
            return r.u16()
        if t == 'UInt32':
            return r.u32()
        if t == 'UInt64':
            return r.u64()
        if t == 'Name':
            return self.fname(pkg, r)
        if t in ('Str', 'Utf8Str', 'AnsiStr'):
            return r.fstring()
        if t == 'Text':
            return self.ftext(pkg, r)
        if t in ('Object', 'Class', 'WeakObject', 'Interface'):
            return self.pkg_index(pkg, r.i32())
        if t in ('SoftObject', 'SoftClass'):
            return self.soft_path(pkg, r)
        if t in ('Array', 'Set'):
            if t == 'Set':
                nrem = r.i32()
                if nrem:
                    raise ValueError('set with removed elements')
            n = r.i32()
            if n < 0 or n > 2000000:
                raise ValueError('bad array count %d' % n)
            inner = p['i']
            if inner['t'] == 'Byte' and not inner.get('e'):
                return {'bytes': n, 'hex': r.take(n)[:64].hex()}
            return [self.read_value(pkg, r, inner) for _ in range(n)]
        if t == 'Map':
            nrem = r.i32()
            if nrem:
                raise ValueError('map with removed keys')
            n = r.i32()
            if n < 0 or n > 2000000:
                raise ValueError('bad map count %d' % n)
            out = []
            for _ in range(n):
                k = self.read_value(pkg, r, p['k'])
                v = self.read_value(pkg, r, p['v'])
                out.append({'key': k, 'value': v})
            return out
        if t == 'Struct':
            return self.read_struct(pkg, r, p.get('s'))
        if t == 'LazyObject':
            return r.take(16).hex()
        if t == 'Optional':
            return self.read_value(pkg, r, p['i']) if r.u8() else None
        raise NotImplementedError('property type %s' % t)

    def fname(self, pkg, r):
        idx, num = r.u32(), r.u32()
        return pkg.name(idx, num)

    def soft_path(self, pkg, r):
        a = self.fname(pkg, r)
        b = self.fname(pkg, r)
        sub = r.fstring()
        if a == 'None' and b == 'None':
            return None
        s = a if b in ('None', '') else '%s.%s' % (a, b)
        return s + (':' + sub if sub else '')

    def ftext(self, pkg, r):
        flags = r.u32()
        ht = r.i8()
        if ht == -1:
            if r.u32():
                return {'text': r.fstring()}
            return {'text': ''}
        if ht == 0:
            ns, key, src = r.fstring(), r.fstring(), r.fstring()
            return {'key': key, 'text': src}
        if ht == 11:  # StringTableEntry
            table = self.fname(pkg, r)
            return {'table': table, 'key': r.fstring()}
        raise NotImplementedError('FText history %d' % ht)

    def read_struct(self, pkg, r, s):
        n = NATIVE.get(s)
        if n is not None:
            return n(self, pkg, r)
        if s not in self.structs:
            raise NotImplementedError('struct %r has no schema' % s)
        return self.read_props(pkg, r, 's', s)

    # ---- exports -------------------------------------------------------------------
    def decode_export(self, pkg, cname, lo, hi):
        d = {}
        r = Reader(pkg.x, lo, hi)
        try:
            d['properties'] = self.read_props(pkg, r, 'c', cname)
        except DecodeError as ex:
            d['properties'] = ex.partial
            d['error'] = ex.partial.pop('!error', 'decode error')
        except Exception as ex:
            d['error'] = '%s: %s' % (type(ex).__name__, ex)
        if 'error' not in d:
            tail = hi - r.p
            # UObject::Serialize ends with a 4-byte "has guid" flag
            if tail >= 4 and pkg.x[r.p:r.p + 4] == ZERO4:
                r.p += 4
                tail -= 4
            extra = EXTRA.get(cname)
            if extra and tail:
                try:
                    d['native'] = extra(self, pkg, r)
                    tail = hi - r.p
                except Exception as ex:
                    d['native_error'] = '%s: %s' % (type(ex).__name__, ex)
            if tail:
                d['tail'] = tail
        return d

    def find_package(self, name):
        """'/Game/X/Y' -> path of Y.uheader under the extracted root (patch chunks win)."""
        if self.root is None or not name.startswith('/Game/'):
            return None
        if self._pkg_index is None:
            self._pkg_index = {}
            chunks = sorted(os.listdir(self.root), key=lambda c: (c.endswith('_P'), c))
            for c in chunks:
                base = os.path.join(self.root, c)
                for proj in os.listdir(base) if os.path.isdir(base) else []:
                    content = os.path.join(base, proj, 'Content')
                    if proj == 'Engine' or not os.path.isdir(content):
                        continue
                    for dp, dn, fn in os.walk(content):
                        rel = dp[len(content):].replace(os.sep, '/')
                        for f in fn:
                            if f.endswith('.uheader'):
                                self._pkg_index['/Game%s/%s' % (rel, f[:-8])] = os.path.join(dp, f)
        return self._pkg_index.get(name)

    def native_parent(self, pkg, super_index, depth=0):
        """Follow a Blueprint class's parent chain (across packages) to its first native class."""
        sup = self.obj_index(pkg, super_index)
        if not sup or depth > 12:
            return None
        if 'script' in sup:
            return sup['script']
        if 'export' in sup:
            i = super_index & 0xFFFFFFFF
            return self.native_parent(pkg, pkg.exports[i]['super'], depth + 1) if i < len(pkg.exports) else None
        key = (sup.get('package'), sup.get('export_hash'))
        if key in self._parent_cache:
            return self._parent_cache[key]
        res = None
        path = self.find_package(str(sup.get('package')))
        if path and sup.get('export_hash'):
            try:
                other = Package(path, headers_only=True)
                for ex in other.exports:
                    if '%016x' % ex['hash'] == sup['export_hash']:
                        res = self.native_parent(other, ex['super'], depth + 1)
                        break
            except Exception:
                res = None
        self._parent_cache[key] = res
        return res

    def decode_package(self, header_path):
        pkg = Package(header_path)
        out = {'package': pkg.package_name, 'exports': []}
        ranges = pkg.export_ranges()
        for i, e in enumerate(pkg.exports):
            cls = self.obj_index(pkg, e['class'])
            d = {'name': e['name'], 'class': cls}
            outer = self.obj_index(pkg, e['outer'])
            if outer:
                d['outer'] = outer
            cname = (cls or {}).get('script')
            if cname is None and e['class'] >> 62 == 0 and (e['class'] & 0xFFFFFFFF) < len(pkg.exports):
                # Object of a Blueprint class defined in this package (usually its default object):
                # decode with the Blueprint's native parent. Variables added by the Blueprint itself
                # are not in the schema; the exact-size check below rejects such exports.
                cname = self.native_parent(pkg, pkg.exports[e['class'] & 0xFFFFFFFF]['super'])
                if cname:
                    d['native_parent'] = cname
            lo, hi = ranges.get(i, (0, 0))
            if cname is None:
                d['error'] = 'class is not native; properties not decoded (%d bytes)' % (hi - lo)
            else:
                best = None
                for variant in self.variants:
                    if cname not in variant['classes']:
                        continue
                    self.use(variant)
                    res = self.decode_export(pkg, cname, lo, hi)
                    res['schema'] = variant['label']
                    if 'error' not in res and 'tail' not in res:
                        best = res
                        break
                    if best is None:
                        best = res
                d.update(best or {'error': 'class %s has no schema' % cname})
            out['exports'].append(d)
        return out


class DecodeError(Exception):
    def __init__(self, partial):
        Exception.__init__(self, partial.get('!error'))
        self.partial = partial


# ---- structs with native (binary) serializers ------------------------------------------------
def _vec3(dec, pkg, r): return [r.f64(), r.f64(), r.f64()]
def _vec2(dec, pkg, r): return [r.f64(), r.f64()]
def _vec4(dec, pkg, r): return [r.f64(), r.f64(), r.f64(), r.f64()]
def _rot(dec, pkg, r): return {'pitch': r.f64(), 'yaw': r.f64(), 'roll': r.f64()}
def _guid(dec, pkg, r): return r.take(16).hex()
def _color(dec, pkg, r): return list(r.take(4))
def _lcolor(dec, pkg, r): return [r.f32(), r.f32(), r.f32(), r.f32()]
def _i64(dec, pkg, r): return r.i64()
def _i32(dec, pkg, r): return r.i32()
def _soft(dec, pkg, r): return dec.soft_path(pkg, r)


def _tag_container(dec, pkg, r):
    n = r.i32()
    if n < 0 or n > 100000:
        raise ValueError('bad tag count %d' % n)
    return [dec.fname(pkg, r) for _ in range(n)]


def _rich_key(dec, pkg, r):
    interp, tangent, weight = r.u8(), r.u8(), r.u8()
    return {'interp': interp, 'time': r.f32(), 'value': r.f32(), 'arrive': r.f32(), 'arrive_w': r.f32(),
            'leave': r.f32(), 'leave_w': r.f32()}


def _per_platform(fn):
    def read(dec, pkg, r):
        cooked = r.u32()
        return fn(r)
    return read


SHARED_BASES = ('DCTableDataAsset', 'DCDataAsset')

NATIVE = {
    'Vector': _vec3, 'Vector2D': _vec2, 'Vector4': _vec4, 'Rotator': _rot, 'Quat': _vec4, 'Plane': _vec4,
    'Vector3f': lambda d, p, r: [r.f32(), r.f32(), r.f32()], 'Vector2f': lambda d, p, r: [r.f32(), r.f32()],
    'Vector_NetQuantize': _vec3, 'Vector_NetQuantize10': _vec3, 'Vector_NetQuantize100': _vec3,
    'Vector_NetQuantizeNormal': _vec3,
    'Guid': _guid, 'Color': _color, 'LinearColor': _lcolor, 'DateTime': _i64, 'Timespan': _i64,
    'IntPoint': lambda d, p, r: [r.i32(), r.i32()], 'IntVector': lambda d, p, r: [r.i32(), r.i32(), r.i32()],
    'FrameNumber': _i32, 'SoftObjectPath': _soft, 'SoftClassPath': _soft,
    'GameplayTagContainer': _tag_container, 'RichCurveKey': _rich_key,
    'PerPlatformFloat': _per_platform(lambda r: r.f32()), 'PerPlatformInt': _per_platform(lambda r: r.i32()),
    'PerPlatformBool': _per_platform(lambda r: bool(r.u8())),
    'Box': lambda d, p, r: {'min': _vec3(d, p, r), 'max': _vec3(d, p, r), 'valid': r.u8()},
    'Box2D': lambda d, p, r: {'min': _vec2(d, p, r), 'max': _vec2(d, p, r), 'valid': r.u8()},
}

EXTRA = {}


def main():
    schema, out_dir = sys.argv[1], sys.argv[2]
    if out_dir == '-':
        # the extracted root is the folder that holds the pakchunk* folders
        parts = os.path.abspath(sys.argv[3]).split(os.sep)
        idx = [i for i, c in enumerate(parts) if c.startswith('pakchunk')]
        root = os.sep.join(parts[:idx[0]]) if idx else None
        json.dump(Decoder(schema, root).decode_package(sys.argv[3]), sys.stdout, indent=1)
        return
    root = sys.argv[3]
    dec = Decoder(schema, root)
    stats = {'packages': 0, 'exports': 0, 'clean': 0, 'error': 0, 'tail': 0}
    errors = {}
    for pat in sys.argv[4:]:
        for hp in glob.glob(os.path.join(root, pat), recursive=True):
            if not hp.endswith('.uheader'):
                continue
            rel = os.path.relpath(hp, root)
            try:
                res = dec.decode_package(hp)
            except Exception as ex:
                res = {'error': '%s: %s' % (type(ex).__name__, ex), 'exports': []}
            stats['packages'] += 1
            for e in res['exports']:
                stats['exports'] += 1
                if 'error' in e:
                    stats['error'] += 1
                    k = e['error'].split(':')[0][:70] if 'not native' in e['error'] else e['error'][:110]
                    errors[k] = errors.get(k, 0) + 1
                elif 'tail' in e:
                    stats['tail'] += 1
                else:
                    stats['clean'] += 1
            op = os.path.join(out_dir, os.path.splitext(rel)[0] + '.json')
            os.makedirs(os.path.dirname(op), exist_ok=True)
            with open(op, 'w', encoding='utf-8') as f:
                json.dump(res, f, indent=1)
    print(json.dumps(stats))
    for k, v in sorted(errors.items(), key=lambda kv: -kv[1])[:25]:
        print('%6d  %s' % (v, k))


if __name__ == '__main__':
    main()
