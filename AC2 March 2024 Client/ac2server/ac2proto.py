"""AC2 UDP transport, derived from the client's own packet capture and the glsclient.dll code.

AC2's network layer is the same family as AC1's. Everything below was verified against a real session
capture (11,657 + 7,067 encrypted packets and 1,093 plain ones, zero mismatches).

Packet = 20-byte little-endian header + payload

    off  size  field
    0    u32   sequence
    4    u32   flags      (see F_* below)
    8    u32   checksum
    12   u16   id         (recipient id)
    14   u16   time
    16   u16   size       (payload length)
    18   u16   iteration

Payload = optional-header blocks (in flag-bit order, see OPTIONAL_BLOCKS) followed by blob fragments:

    fragment = u32 seq, u32 id, u16 count, u16 size (includes this 16-byte header), u16 index, u16 group, data
    message id = 0x23000000 + seq + 1; a message is `count` fragments, up to MAX_FRAG_DATA bytes each.

Checksum
    H = Hash32(header with its checksum slot set to 0xBADD70DD)
    P = sum of Hash32 over every optional block and over every fragment's header and data separately
    plain packets:                     checksum = H + P
    packets with F_ENCRYPTED_CRC:      checksum = H + (P ^ key)
    key = ISAAC(seed) value number (seq - 2): the stream is read from the top of each 256-value block
    downward. Server->client packets use the server seed, client->server the client seed (both are sent in
    the ConnectRequest).
"""
import struct
from dataclasses import dataclass

HEADER = struct.Struct("<IIIHHHH")
HEADER_LEN = HEADER.size  # 20
M32 = 0xFFFFFFFF

# Packet flags (names follow AC1; values confirmed against the capture)
F_RETRANSMISSION = 0x00000001
F_ENCRYPTED_CRC = 0x00000002
F_BLOB_FRAGMENTS = 0x00000004
F_ACK_SEQUENCE = 0x00004000
F_LOGIN_REQUEST = 0x00010000
F_CONNECT_REQUEST = 0x00040000
F_CONNECT_RESPONSE = 0x00080000
F_TIME_SYNC = 0x01000000
F_ECHO_REQUEST = 0x02000000
F_ECHO_RESPONSE = 0x04000000
F_FLOW = 0x08000000

ENCRYPTED_CHECKSUM_BIT = F_ENCRYPTED_CRC  # older name used by server.py

# (flag, size in bytes) in the order the blocks appear in the payload
OPTIONAL_BLOCKS = ((F_ACK_SEQUENCE, 4), (F_TIME_SYNC, 8), (F_ECHO_REQUEST, 4),
                   (F_ECHO_RESPONSE, 8), (F_FLOW, 6))

FRAG_HEADER = struct.Struct("<IIHHHH")
FRAG_HEADER_LEN = FRAG_HEADER.size  # 16
MAX_FRAG_DATA = 448


@dataclass
class Packet:
    seq: int
    flags: int
    checksum: int
    id: int
    time: int
    length: int
    iteration: int
    payload: bytes

    @property
    def well_formed(self) -> bool:
        return self.length == len(self.payload)


def parse(datagram: bytes) -> Packet | None:
    if len(datagram) < HEADER_LEN:
        return None
    seq, flags, crc, pid, ptime, length, it = HEADER.unpack_from(datagram)
    return Packet(seq, flags, crc, pid, ptime, length, it, datagram[HEADER_LEN:])


# ------------------------------------------------------------------ checksum
CHECKSUM_SEED = 0xBADD70DD


def hash32(data: bytes) -> int:
    """AC1-family Hash32; matches the routine at glsclient.dll+0x521a0."""
    n = len(data)
    c = (n << 16) & M32
    i = 0
    while i + 4 <= n:
        c = (c + struct.unpack_from("<I", data, i)[0]) & M32
        i += 4
    shift = 3
    for j in range(i, n):
        c = (c + (data[j] << (8 * shift))) & M32
        shift -= 1
    return c


def header_hash(datagram: bytes) -> int:
    hdr = bytearray(datagram[:HEADER_LEN])
    struct.pack_into("<I", hdr, 8, CHECKSUM_SEED)
    return hash32(bytes(hdr))


def compute_checksum(datagram: bytes) -> int:
    """Checksum for a plain packet whose payload is a single block (handshake packets, ack-only packets)."""
    return (header_hash(datagram) + hash32(datagram[HEADER_LEN:])) & M32


# ------------------------------------------------------------------- ISAAC
class ISAAC:
    """ISAAC exactly as glsclient.dll builds it: aa = bb = cc = seed, randrsl all zero, randinit(flag=1)."""

    def __init__(self, seed: int):
        self.mm = [0] * 256
        self.rsl = [0] * 256
        self.aa = self.bb = self.cc = seed & M32
        self._init()

    @staticmethod
    def _mix(v):
        a, b, c, d, e, f, g, h = v
        a ^= (b << 11) & M32; d = (d + a) & M32; b = (b + c) & M32
        b ^= c >> 2;          e = (e + b) & M32; c = (c + d) & M32
        c ^= (d << 8) & M32;  f = (f + c) & M32; d = (d + e) & M32
        d ^= e >> 16;         g = (g + d) & M32; e = (e + f) & M32
        e ^= (f << 10) & M32; h = (h + e) & M32; f = (f + g) & M32
        f ^= g >> 4;          a = (a + f) & M32; g = (g + h) & M32
        g ^= (h << 8) & M32;  b = (b + g) & M32; h = (h + a) & M32
        h ^= a >> 9;          c = (c + h) & M32; a = (a + b) & M32
        return [a, b, c, d, e, f, g, h]

    def _init(self):
        v = [0x9E3779B9] * 8
        for _ in range(4):
            v = self._mix(v)
        for i in range(0, 256, 8):
            v = [(v[j] + self.rsl[i + j]) & M32 for j in range(8)]
            v = self._mix(v)
            self.mm[i:i + 8] = v
        for i in range(0, 256, 8):
            v = [(v[j] + self.mm[i + j]) & M32 for j in range(8)]
            v = self._mix(v)
            self.mm[i:i + 8] = v
        self.generate()

    def generate(self):
        self.cc = (self.cc + 1) & M32
        self.bb = (self.bb + self.cc) & M32
        mm, rsl = self.mm, self.rsl
        for i in range(256):
            x = mm[i]
            r = i & 3
            if r == 0:
                self.aa ^= (self.aa << 13) & M32
            elif r == 1:
                self.aa ^= self.aa >> 6
            elif r == 2:
                self.aa ^= (self.aa << 2) & M32
            else:
                self.aa ^= self.aa >> 16
            self.aa = (mm[(i + 128) & 255] + self.aa) & M32
            y = (mm[(x >> 2) & 255] + self.aa + self.bb) & M32
            mm[i] = y
            self.bb = (mm[(y >> 10) & 255] + x) & M32
            rsl[i] = self.bb


class KeyStream:
    """Checksum keys by packet sequence number (random access, so retransmits and reordering just work)."""

    def __init__(self, seed: int):
        self._g = ISAAC(seed)
        self._blocks = [list(self._g.rsl)]

    def key(self, seq: int) -> int:
        rank = seq - 2
        if rank < 0:
            raise ValueError("encrypted packets start at sequence 2")
        blk, i = divmod(rank, 256)
        while len(self._blocks) <= blk:
            self._g.generate()
            self._blocks.append(list(self._g.rsl))
        return self._blocks[blk][255 - i]


# --------------------------------------------------------------- fragments
@dataclass
class Fragment:
    seq: int          # message sequence number (message id = 0x23000000 + seq + 1)
    id: int
    count: int
    index: int
    group: int
    data: bytes

    def header_bytes(self) -> bytes:
        return FRAG_HEADER.pack(self.seq, self.id, self.count, FRAG_HEADER_LEN + len(self.data),
                                self.index, self.group)

    def pack(self) -> bytes:
        return self.header_bytes() + self.data


def split_payload(flags: int, payload: bytes):
    """Split a payload into (optional blocks, fragments). Returns None if it doesn't parse."""
    off, opts = 0, []
    for flag, size in OPTIONAL_BLOCKS:
        if flags & flag:
            if off + size > len(payload):
                return None
            opts.append(payload[off:off + size])
            off += size
    frags = []
    if flags & F_BLOB_FRAGMENTS:
        while off < len(payload):
            if off + FRAG_HEADER_LEN > len(payload):
                return None
            seq, fid, count, size, index, group = FRAG_HEADER.unpack_from(payload, off)
            if size < FRAG_HEADER_LEN or off + size > len(payload):
                return None
            frags.append(Fragment(seq, fid, count, index, group, payload[off + FRAG_HEADER_LEN:off + size]))
            off += size
    elif off != len(payload):
        return None
    return opts, frags


def payload_hash(opts, frags) -> int:
    total = sum(hash32(o) for o in opts)
    for f in frags:
        total += hash32(f.header_bytes()) + hash32(f.data)
    return total & M32


def build_packet(seq: int, flags: int, opts, frags, *, id: int = 0, time: int = 0, iteration: int = 0,
                 keys: KeyStream | None = None) -> bytes:
    """Build a data packet. `keys` is required when flags has F_ENCRYPTED_CRC."""
    body = b"".join(opts) + b"".join(f.pack() for f in frags)
    pkt = HEADER.pack(seq, flags, 0, id, time & 0xFFFF, len(body), iteration) + body
    h, p = header_hash(pkt), payload_hash(opts, frags)
    crc = (h + ((p ^ keys.key(seq)) if flags & F_ENCRYPTED_CRC else p)) & M32
    return pkt[:8] + struct.pack("<I", crc) + pkt[12:]


def check_packet(datagram: bytes, keys: KeyStream | None) -> str:
    """'ok', 'bad', or 'unparsed' for a received data packet (handshake packets: use compute_checksum)."""
    p = parse(datagram)
    parts = split_payload(p.flags, p.payload)
    if parts is None:
        return "unparsed"
    h, pl = header_hash(datagram), payload_hash(*parts)
    if p.flags & F_ENCRYPTED_CRC:
        if keys is None or p.seq < 2:
            return "bad"
        want = (h + (pl ^ keys.key(p.seq))) & M32
    else:
        want = (h + pl) & M32
    return "ok" if want == p.checksum else "bad"


# ------------------------------------------------------- handshake packets
def build(seq: int, flags: int, payload: bytes, *, id: int = 0, time: int = 0, iteration: int = 0) -> bytes:
    """Build a handshake-style packet (single-block payload, plain checksum)."""
    pkt = HEADER.pack(seq, flags, 0, id, time & 0xFFFF, len(payload), iteration) + payload
    return pkt[:8] + struct.pack("<I", compute_checksum(pkt)) + pkt[12:]


def connect_request_payload(server_time: float, cookie: int, client_id: int,
                            server_seed: int, client_seed: int) -> bytes:
    """32-byte ConnectRequest body, layout taken from the real server's reply in the capture."""
    return struct.pack("<dQIIII", server_time, cookie, client_id, server_seed, client_seed, 0)
