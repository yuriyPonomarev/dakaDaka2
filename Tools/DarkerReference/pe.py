"""Minimal read-only PE64 image reader: sections, VA <-> file offset, pointer search.

Standard library only. Nothing in the image is executed."""
import mmap
import struct


class PE:
    def __init__(self, path):
        self.f = open(path, 'rb')
        self.mm = mmap.mmap(self.f.fileno(), 0, access=mmap.ACCESS_READ)
        mm = self.mm
        pe = struct.unpack_from('<I', mm, 0x3c)[0]
        if mm[pe:pe + 4] != b'PE\0\0':
            raise ValueError('not a PE image')
        nsec = struct.unpack_from('<H', mm, pe + 6)[0]
        optsz = struct.unpack_from('<H', mm, pe + 20)[0]
        opt = pe + 24
        if struct.unpack_from('<H', mm, opt)[0] != 0x20b:
            raise ValueError('not a PE32+ image')
        self.base = struct.unpack_from('<Q', mm, opt + 24)[0]
        self.sections = []
        so = opt + optsz
        for _ in range(nsec):
            name = mm[so:so + 8].rstrip(b'\0').decode('latin1')
            vsize, va, rsize, rptr = struct.unpack_from('<IIII', mm, so + 8)
            self.sections.append((name, self.base + va, vsize, rptr, rsize))
            so += 40
        self._last = self.sections[0]

    def _find(self, va):
        s = self._last
        if s[1] <= va < s[1] + s[4]:
            return s
        for s in self.sections:
            if s[1] <= va < s[1] + s[4]:
                self._last = s
                return s
        return None

    def off(self, va):
        s = self._find(va)
        return None if s is None else s[3] + va - s[1]

    def va(self, off):
        for name, sva, vsize, rptr, rsize in self.sections:
            if rptr <= off < rptr + rsize:
                return sva + off - rptr
        return None

    def sec(self, va):
        s = self._find(va) if va else None
        return None if s is None else s[0]

    def q(self, va):
        o = self.off(va)
        return None if o is None else struct.unpack_from('<Q', self.mm, o)[0]

    def u32(self, va):
        o = self.off(va)
        return None if o is None else struct.unpack_from('<I', self.mm, o)[0]

    def u16(self, va):
        o = self.off(va)
        return None if o is None else struct.unpack_from('<H', self.mm, o)[0]

    def cstr(self, va, maxlen=256):
        o = self.off(va) if va else None
        if o is None:
            return None
        e = self.mm.find(b'\0', o, o + maxlen)
        if e <= o:
            return None
        b = self.mm[o:e]
        for c in b:
            if c < 0x20 or c > 0x7e:
                return None
        return b.decode('ascii')

    def wstr(self, va, maxlen=200):
        o = self.off(va) if va else None
        if o is None:
            return None
        out = []
        for i in range(maxlen):
            c = struct.unpack_from('<H', self.mm, o + 2 * i)[0]
            if c == 0:
                break
            if c < 0x20 or c > 0x7e:
                return None
            out.append(chr(c))
        return ''.join(out) if len(out) >= 2 else None

    def find_cstr(self, s):
        """VAs where the exact NUL-terminated ASCII string starts."""
        needle = b'\0' + s.encode() + b'\0'
        out = []
        p = 0
        while True:
            p = self.mm.find(needle, p)
            if p < 0:
                break
            v = self.va(p + 1)
            if v is not None:
                out.append(v)
            p += 1
        return out

    def find_ptr(self, va, secnames=('.rdata', '.data')):
        """VAs of 8-aligned slots holding a pointer equal to va."""
        needle = struct.pack('<Q', va)
        out = []
        for name, sva, vsize, rptr, rsize in self.sections:
            if name not in secnames:
                continue
            p = rptr
            end = rptr + rsize
            while True:
                p = self.mm.find(needle, p, end)
                if p < 0:
                    break
                if (p - rptr) % 8 == 0:
                    out.append(sva + p - rptr)
                p += 1
        return out
