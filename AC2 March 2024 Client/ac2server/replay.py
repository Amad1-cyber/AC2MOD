"""The message stream of a recorded real session, reassembled from the client's PacketLogger pcap.

The server uses this to answer the client the way the real server did (lock-step replay): after the n-th
client message arrives, send every server message the real server sent between client message n and n+1.
"""
import glob
import os
import struct
from dataclasses import dataclass

import ac2proto as proto


@dataclass
class Event:
    who: str        # 'S' = server->client, 'C' = client->server
    group: int
    data: bytes     # complete message: u16 opcode, u16 version, body
    fseq: int = 0   # the message sequence number the real server gave it (fragment header seq)
    fid: int = 0    # ... and its message id (0x23000000 + seq + 1 for the server)

    @property
    def opcode(self) -> int:
        return struct.unpack_from("<H", self.data, 0)[0] if len(self.data) >= 2 else -1


def find_capture(client_dir: str) -> str | None:
    files = glob.glob(os.path.join(client_dir, "pkt_*_log.pcap"))
    files = [f for f in files if os.path.getsize(f) > 100_000]
    return max(files, key=os.path.getsize) if files else None


def load(path: str) -> list[Event]:
    """Reassemble every message in a PacketLogger pcap, in the order each one completed."""
    data = open(path, "rb").read()
    off = 24
    events: list[Event] = []
    pending = {"S": {}, "C": {}}
    prev = {"S": None, "C": None}
    while off + 16 <= len(data):
        _, _, caplen, _ = struct.unpack_from("<IIII", data, off)
        off += 16
        pkt, off = data[off:off + caplen], off + caplen
        ip = pkt[14:]
        if len(pkt) < 42 or ip[9] != 17:
            continue
        ihl = (ip[0] & 15) * 4
        sport, _dport = struct.unpack(">HH", ip[ihl:ihl + 4])
        who = "C" if sport == 12345 else "S"
        dg = ip[ihl + 8:]
        if dg == prev[who]:            # the logger writes client sends twice
            continue
        prev[who] = dg
        p = proto.parse(dg)
        if p is None or p.flags & (proto.F_LOGIN_REQUEST | proto.F_CONNECT_REQUEST | proto.F_CONNECT_RESPONSE):
            continue
        parts = proto.split_payload(p.flags, p.payload)
        if parts is None:
            continue
        for f in parts[1]:
            slot = pending[who].setdefault((f.id, f.seq), {})
            slot[f.index] = f.data
            if len(slot) == f.count:
                events.append(Event(who, f.group, b"".join(slot[k] for k in sorted(slot)), f.seq, f.id))
                del pending[who][(f.id, f.seq)]
    return events


def server_runs(events: list[Event]) -> list[list[Event]]:
    """runs[k] = the server messages that follow the k-th client message (runs[0]: before the first one)."""
    runs: list[list[Event]] = [[]]
    for e in events:
        if e.who == "C":
            runs.append([])
        else:
            runs[-1].append(e)
    return runs
