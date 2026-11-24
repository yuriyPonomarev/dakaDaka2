"""CityHash64 (v1.1) in pure Python, as used by Unreal for name hashes and script object ids.

Self-check: `python cityhash.py` compares against values read from real packages."""
import struct

M = 0xFFFFFFFFFFFFFFFF
k0 = 0xc3a5c85c97cb3127
k1 = 0xb492b66fbe98f273
k2 = 0x9ae16a3b2f90404f


def _f64(s, i):
    return struct.unpack_from('<Q', s, i)[0]


def _f32(s, i):
    return struct.unpack_from('<I', s, i)[0]


def _rot(v, n):
    return v if n == 0 else ((v >> n) | (v << (64 - n))) & M


def _smix(v):
    return v ^ (v >> 47)


def _bswap(v):
    return struct.unpack('<Q', struct.pack('>Q', v))[0]


def _h16(u, v, mul=0x9ddfea08eb382d69):
    a = ((u ^ v) * mul) & M
    a ^= a >> 47
    b = ((v ^ a) * mul) & M
    b ^= b >> 47
    return (b * mul) & M


def _h0to16(s):
    n = len(s)
    if n >= 8:
        mul = (k2 + n * 2) & M
        a = (_f64(s, 0) + k2) & M
        b = _f64(s, n - 8)
        c = (_rot(b, 37) * mul + a) & M
        d = ((_rot(a, 25) + b) * mul) & M
        return _h16(c, d, mul)
    if n >= 4:
        mul = (k2 + n * 2) & M
        a = _f32(s, 0)
        return _h16((n + (a << 3)) & M, _f32(s, n - 4), mul)
    if n > 0:
        a, b, c = s[0], s[n >> 1], s[n - 1]
        y = (a + (b << 8)) & 0xFFFFFFFF
        z = (n + (c << 2)) & 0xFFFFFFFF
        return (_smix(((y * k2) & M) ^ ((z * k0) & M)) * k2) & M
    return k2


def _h17to32(s):
    n = len(s)
    mul = (k2 + n * 2) & M
    a = (_f64(s, 0) * k1) & M
    b = _f64(s, 8)
    c = (_f64(s, n - 8) * mul) & M
    d = (_f64(s, n - 16) * k2) & M
    return _h16((_rot((a + b) & M, 43) + _rot(c, 30) + d) & M, (a + _rot((b + k2) & M, 18) + c) & M, mul)


def _weak(w, x, y, z, a, b):
    a = (a + w) & M
    b = _rot((b + a + z) & M, 21)
    c = a
    a = (a + x) & M
    a = (a + y) & M
    b = (b + _rot(a, 44)) & M
    return (a + z) & M, (b + c) & M


def _weak_s(s, i, a, b):
    return _weak(_f64(s, i), _f64(s, i + 8), _f64(s, i + 16), _f64(s, i + 24), a, b)


def _h33to64(s):
    n = len(s)
    mul = (k2 + n * 2) & M
    a = (_f64(s, 0) * k2) & M
    b = _f64(s, 8)
    c = _f64(s, n - 24)
    d = _f64(s, n - 32)
    e = (_f64(s, 16) * k2) & M
    f = (_f64(s, 24) * 9) & M
    g = _f64(s, n - 8)
    h = (_f64(s, n - 16) * mul) & M
    u = (_rot((a + g) & M, 43) + ((_rot(b, 30) + c) * 9)) & M
    v = ((((a + g) & M) ^ d) + f + 1) & M
    w = (_bswap(((u + v) * mul) & M) + h) & M
    x = (_rot((e + f) & M, 42) + c) & M
    y = ((_bswap(((v + w) * mul) & M) + g) * mul) & M
    z = (e + f + c) & M
    a = (_bswap(((x + z) * mul + y) & M) + b) & M
    b = (_smix(((z + a) * mul + d + h) & M) * mul) & M
    return (b + x) & M


def cityhash64(s):
    n = len(s)
    if n <= 16:
        return _h0to16(s)
    if n <= 32:
        return _h17to32(s)
    if n <= 64:
        return _h33to64(s)
    x = _f64(s, n - 40)
    y = (_f64(s, n - 16) + _f64(s, n - 56)) & M
    z = _h16((_f64(s, n - 48) + n) & M, _f64(s, n - 24))
    v = _weak_s(s, n - 64, n, z)
    w = _weak_s(s, n - 32, (y + k1) & M, x)
    x = (x * k1 + _f64(s, 0)) & M
    left = (n - 1) & ~63
    i = 0
    while True:
        x = (_rot((x + y + v[0] + _f64(s, i + 8)) & M, 37) * k1) & M
        y = (_rot((y + v[1] + _f64(s, i + 48)) & M, 42) * k1) & M
        x ^= w[1]
        y = (y + v[0] + _f64(s, i + 40)) & M
        z = (_rot((z + w[0]) & M, 33) * k1) & M
        v = _weak_s(s, i, (v[1] * k1) & M, (x + w[0]) & M)
        w = _weak_s(s, i + 32, (z + w[1]) & M, (y + _f64(s, i + 16)) & M)
        z, x = x, z
        i += 64
        left -= 64
        if left == 0:
            break
    return _h16((_h16(v[0], w[0]) + ((_smix(y) * k1) & M) + z) & M, (_h16(v[1], w[1]) + x) & M)


def script_object_id(path):
    """FPackageObjectIndex of a script object: '/Script/Pkg.Class' -> 62-bit hash | ScriptImport."""
    p = path.replace('.', '/').replace(':', '/').lower()
    h = cityhash64(p.encode('utf-16-le'))
    return (h & ~(3 << 62)) | (1 << 62)


if __name__ == '__main__':
    ok = cityhash64(b'id_constant_advpointplayerkill') == 0x7a59cdcb6665c1bf
    ok &= script_object_id('/Script/DungeonCrawler.DCMeleeAttackDataAsset') == 0x43e37af978d8293c
    ok &= script_object_id('/Script/DungeonCrawler') == 0x546c83a368b135e2
    print('cityhash self-check: %s' % ('OK' if ok else 'FAILED'))
    raise SystemExit(0 if ok else 1)
