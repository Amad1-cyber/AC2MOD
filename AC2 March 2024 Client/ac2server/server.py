"""AC2 server: UDP transport (handshake, ISAAC-keyed checksums, acks, echo) + lock-step replay of a recorded
real session, so the real client can be driven through login without a full game-logic server.

Usage:
    python server.py [--host 0.0.0.0] [--ports 7777 7778] [--capture session.pcap]

The client sends its LoginRequest and data to the main port (7777) and its ConnectResponse to main+1 (7778);
the server answers the LoginRequest from 7777 and sends all later data from 7778, like the real server did.
"""
import argparse
import asyncio
import logging
import os
import random
import struct
import time
from collections import deque

import ac2proto as proto
import replay

log = logging.getLogger("ac2server")
HERE = os.path.dirname(os.path.abspath(__file__))
CLIENT_DIR = os.path.dirname(HERE)

SERVER_ID = 0x00F1
CLIENT_ID = 0x0101
TIME_BASE = 4_261_972.0          # server clock offset, similar to the capture
PACE_BATCH, PACE_SLEEP = 16, 0.004   # roughly 4000 packets/s; the client's socket buffer is small
DETAILED_PACKETS = 40            # log this many client data packets in full per session
RAW_LOG_LIMIT = 400


class Session:
    def __init__(self, server: "Server", addr, port: int, login_payload: bytes):
        self.server, self.addr, self.port, self.login_payload = server, addr, port, login_payload
        self.cookie = random.getrandbits(64)
        self.sseed, self.cseed = random.getrandbits(32), random.getrandbits(32)
        self.skeys, self.ckeys = proto.KeyStream(self.sseed), proto.KeyStream(self.cseed)
        self.t0 = time.time()
        self.connected = False
        self.seq = 2                 # next server data-packet sequence (encrypted packets start at 2)
        self.msg_seq = 0             # next server message sequence
        self.client_seq = 0          # highest client data sequence seen
        self.client_ack = 0          # highest server sequence the client has acknowledged
        self.rx: dict = {}           # (fragment id, seq) -> {index: data} while reassembling client messages
        self.live_name = b""         # name field of the character the client just created
        self.c_count = 0             # complete client messages received
        self.c_packets = 0
        self.out: deque = deque()    # packets waiting to be paced out
        self.pump_task = None

    # -------------------------------------------------------------- helpers
    @property
    def data_port(self) -> int:
        return self.port + 1

    def server_time(self) -> float:
        return TIME_BASE + (time.time() - self.t0)

    def stamp(self) -> int:
        return int(self.server_time() * 10) & 0xFFFF

    # ------------------------------------------------------------ handshake
    def send_connect_request(self, port: int):
        body = proto.connect_request_payload(self.server_time(), self.cookie, CLIENT_ID, self.sseed, self.cseed)
        pkt = proto.build(0, proto.F_CONNECT_REQUEST, body, id=SERVER_ID, time=self.stamp(), iteration=CLIENT_ID)
        log.info("[%d] -> %s ConnectRequest (cookie %016x, seeds s=%08x c=%08x)",
                 port, self.addr, self.cookie, self.sseed, self.cseed)
        self.server.send(port, self.addr, pkt)

    def on_connect_response(self, p: proto.Packet, port: int):
        ok = p.payload[:8] == struct.pack("<Q", self.cookie)
        if self.connected:
            return                               # the client repeats it until it hears from us
        log.info("[%d] ConnectResponse cookie %s", port, "MATCHES" if ok else "does NOT match")
        if not ok:
            return
        self.connected = True
        log.info("handshake complete - starting replay (%d recorded server messages before the first client reply)",
                 len(self.server.runs[0]))
        self.start_replay()

    # ----------------------------------------------------------- sending
    def _queue(self, flags: int, opts, frags):
        seq = self.seq
        self.seq += 1
        pkt = proto.build_packet(seq, flags, opts, frags, id=SERVER_ID, time=self.stamp(),
                                 iteration=CLIENT_ID, keys=self.skeys)
        self.out.append(pkt)

    def _fragments(self, e: replay.Event, fresh: bool = False):
        """Fragment a message. Recorded messages keep the real server's numbering; `fresh` ones (anything we
        build or re-send) take the next free message sequence number."""
        if self.server.keep_ids and e.fid and not fresh:
            seq, mid = e.fseq, e.fid
            self.msg_seq = max(self.msg_seq, seq + 1)
        else:
            seq = self.msg_seq
            self.msg_seq += 1
            mid = 0x23000000 + seq + 1
        chunks = [e.data[i:i + proto.MAX_FRAG_DATA] for i in range(0, len(e.data), proto.MAX_FRAG_DATA)] or [b""]
        return [proto.Fragment(seq, mid, len(chunks), i, e.group, c) for i, c in enumerate(chunks)]

    def queue_message(self, e: replay.Event, fresh: bool = False):
        for f in self._fragments(e, fresh):
            self._queue(proto.F_BLOB_FRAGMENTS | proto.F_ENCRYPTED_CRC, [], [f])

    def send_events(self, events, fresh: bool = False):
        for e in events:
            self.queue_message(e, fresh)
        self.ensure_pump()

    def send_message(self, data: bytes, group: int = 1):
        self.queue_message(replay.Event("S", group, data), fresh=True)
        self.ensure_pump()

    def start_replay(self):
        run0 = self.server.runs[0]
        small = [e for e in run0[:2] if len(e.data) <= proto.MAX_FRAG_DATA]
        if len(small) == 2:
            # like the real server: ack + time sync + the first two (small) messages in one packet
            frags = self._fragments(small[0]) + self._fragments(small[1])
            self._queue(proto.F_ACK_SEQUENCE | proto.F_TIME_SYNC | proto.F_BLOB_FRAGMENTS | proto.F_ENCRYPTED_CRC,
                        [struct.pack("<I", 1), struct.pack("<d", self.server_time())], frags)
            run0 = run0[2:]
        for e in run0:
            self.queue_message(e)
        self.ensure_pump()

    def ensure_pump(self):
        if self.pump_task is None or self.pump_task.done():
            self.pump_task = asyncio.get_running_loop().create_task(self._pump())

    async def _pump(self):
        sent = 0
        while self.out:
            for _ in range(min(PACE_BATCH, len(self.out))):
                self.server.send(self.data_port, self.addr, self.out.popleft())
                sent += 1
            await asyncio.sleep(PACE_SLEEP)
        log.info("sent %d packets (server seq now %d)", sent, self.seq)

    def send_ack(self):
        pkt = proto.build_packet(self.seq - 1, proto.F_ACK_SEQUENCE, [struct.pack("<I", self.client_seq)], [],
                                 id=SERVER_ID, time=self.stamp(), iteration=CLIENT_ID)
        self.server.send(self.data_port, self.addr, pkt)

    # ---------------------------------------------------------- receiving
    def on_data(self, p: proto.Packet, data: bytes, port: int):
        parts = proto.split_payload(p.flags, p.payload)
        status = proto.check_packet(data, self.ckeys)
        self.c_packets += 1
        if self.c_packets <= DETAILED_PACKETS or status != "ok":
            log.info("[%d] client packet seq=%d flags=%08x crc=%s len=%d%s", port, p.seq, p.flags, status,
                     p.length, "" if parts else "  (payload not understood: %s)" % p.payload[:24].hex(" "))
        elif self.c_packets % 200 == 0:
            log.info("... %d client packets so far, %d messages", self.c_packets, self.c_count)
        if parts is None:
            return
        opts, frags = parts
        off = 0
        for flag, size in proto.OPTIONAL_BLOCKS:
            if not p.flags & flag:
                continue
            blk = p.payload[off:off + size]
            off += size
            if flag == proto.F_ACK_SEQUENCE:
                self.client_ack = max(self.client_ack, struct.unpack("<I", blk)[0])
            elif flag == proto.F_ECHO_REQUEST:                  # keep-alive: echo the value back
                self._queue(proto.F_ECHO_RESPONSE | proto.F_ENCRYPTED_CRC,
                            [struct.pack("<ff", struct.unpack("<f", blk)[0], 0.001)], [])
                self.ensure_pump()
        if frags:
            self.client_seq = max(self.client_seq, p.seq)
            for f in frags:
                slot = self.rx.setdefault((f.id, f.seq), {})
                slot[f.index] = f.data
                if len(slot) == f.count:
                    msg = b"".join(slot[k] for k in sorted(slot))
                    del self.rx[(f.id, f.seq)]
                    self.on_message(msg, f.group)
            self.send_ack()

    def on_message(self, msg: bytes, group: int):
        self.c_count += 1
        op = struct.unpack_from("<H", msg, 0)[0] if len(msg) >= 2 else -1
        if self.c_count <= 30 or self.c_count % 100 == 0:
            log.info("client message #%d: opcode 0x%04x, %d bytes", self.c_count, op, len(msg))
        if self.c_count <= 12:
            try:
                with open(os.path.join(HERE, "client_messages.log"), "a") as fh:
                    fh.write(f"{self.c_count} {op:04x} {len(msg)} {msg.hex()}\n")
            except OSError:
                pass
        handler = {0x38: self.h_hello, 0x3B: self.h_ready, 0x2A: self.h_create, 0x2B: self.h_enter}.get(op)
        if handler:
            handler(msg)
        elif self.c_count <= 40:
            log.info("  (no handler for opcode 0x%04x yet)", op)

    # ------------------------------------------------ message handlers
    # The recording's batches (runs[k] = what the real server sent after the client's k-th message):
    #   runs[1] bulk world-library data   runs[2] account info   runs[3] character list   runs[4] world entry
    def h_hello(self, msg):                 # 0x38: client hello
        self.send_events(self.server.runs[1])

    def h_ready(self, msg):                 # 0x3b: client ready -> account info (an account with no characters)
        self.send_events(self.server.runs[2])

    @staticmethod
    def name_field(msg: bytes) -> bytes:
        """The character-name string of a 0x2a create request: u16 count + UTF-16 chars (obfuscated), padded to 4.
        It follows the account string (u16 count + bytes, padded to 4)."""
        off = 4
        n1 = struct.unpack_from("<H", msg, off)[0]
        off += (2 + n1 + 3) & ~3
        n2 = struct.unpack_from("<H", msg, off)[0]
        return msg[off:off + ((2 + 2 * n2 + 3) & ~3)]

    def personalise(self, events):
        """Swap the recorded character's name for the one the client just created (same-length names only)."""
        rec, live = self.server.rec_name, self.live_name
        if not rec or not live or len(rec) != len(live):
            return events
        return [replay.Event(e.who, e.group, e.data.replace(rec, live), e.fseq, e.fid) for e in events]

    def h_create(self, msg):                # 0x2a: create character -> "created" reply (recorded character)
        self.live_name = self.name_field(msg)
        log.info("create-character request (%d bytes), name field %s; recorded name field %s", len(msg),
                 self.live_name.hex(" "), (self.server.rec_name or b"").hex(" "))
        self.send_events(self.personalise(self.server.runs[3]), fresh=True)

    def h_enter(self, msg):                 # 0x2b: enter world with a character id
        cid = struct.unpack_from("<Q", msg, 4)[0]
        log.info("enter world with character id %016x", cid)
        asyncio.get_running_loop().create_task(self._enter_world())

    async def _enter_world(self):
        """Send the recorded world-entry batch. AC2_ENTER_STEP=<seconds> delivers one message at a time so a
        client crash can be pinned on a specific message (see the log)."""
        step = float(os.environ.get("AC2_ENTER_STEP", "0"))
        batch = self.personalise(self.server.runs[4])
        order = os.environ.get("AC2_ENTER_ORDER", "orig")       # experiments: orig | skip68 | last68
        if order == "skip68":
            batch = [e for e in batch if e.opcode != 0x68]
        elif order == "last68":
            batch = [e for e in batch if e.opcode != 0x68] + [e for e in batch if e.opcode == 0x68]
        for i, e in enumerate(batch, 1):
            if step:
                log.info("world-entry message %d/%d: opcode 0x%04x, %d bytes", i, len(batch), e.opcode, len(e.data))
            self.queue_message(e, fresh=True)
            self.ensure_pump()
            if step:
                await asyncio.sleep(step)


class Server:
    def __init__(self, events, max_run: int = 10**9, keep_ids: bool = True):
        self.max_run = max_run        # replay only the first N batches of recorded server messages
        self.keep_ids = keep_ids      # reuse the recorded message numbers instead of renumbering
        self.events = events
        self.runs = replay.server_runs(events) if events else [[]]
        # the recorded character's name field, as it appears in the recorded 0x57 "character created" reply
        self.rec_name = b""
        for e in (self.runs[3] if len(self.runs) > 3 else []):
            if e.opcode == 0x57 and len(e.data) > 16:
                n = struct.unpack_from("<H", e.data, 16)[0]
                self.rec_name = e.data[16:16 + ((2 + 2 * n + 3) & ~3)]
        self.sessions: dict = {}
        self.transports: dict = {}
        self.raw_lines = 0

    def send(self, port: int, addr, pkt: bytes):
        t = self.transports.get(port)
        if t is not None:
            t.sendto(pkt, addr)

    def raw(self, port, addr, data: bytes):
        if self.raw_lines < RAW_LOG_LIMIT:
            self.raw_lines += 1
            try:
                with open("raw_packets.log", "a") as fh:
                    fh.write(f"{port} {addr[0]}:{addr[1]} {data.hex()}\n")
            except OSError:
                pass

    def on_datagram(self, port: int, data: bytes, addr):
        p = proto.parse(data)
        if p is None:
            log.warning("[%d] %s: runt datagram (%d bytes)", port, addr, len(data))
            return
        if p.flags & proto.F_LOGIN_REQUEST:
            self.raw(port, addr, data)
            ok = proto.compute_checksum(data) == p.checksum
            s = self.sessions.get(addr)
            if s is None or s.connected or s.login_payload != p.payload:
                log.info("[%d] LoginRequest from %s crc=%s (%d bytes) - new session", port, addr,
                         "OK" if ok else "BAD", len(data))
                s = self.sessions[addr] = Session(self, addr, port, p.payload)
            s.send_connect_request(port)
            return
        s = self.sessions.get(addr)
        if s is None:
            log.info("[%d] packet from unknown peer %s (flags %08x) ignored", port, addr, p.flags)
            return
        if p.flags & proto.F_CONNECT_RESPONSE:
            s.on_connect_response(p, port)
        else:
            s.on_data(p, data, port)


class AC2Protocol(asyncio.DatagramProtocol):
    def __init__(self, server: Server, port: int):
        self.server, self.port = server, port

    def connection_made(self, transport):
        self.server.transports[self.port] = transport
        log.info("listening on UDP %d", self.port)

    def datagram_received(self, data, addr):
        try:
            self.server.on_datagram(self.port, data, addr)
        except Exception:                                # noqa: BLE001 - never let one packet kill the server
            log.exception("error handling packet from %s", addr)

    def error_received(self, exc):
        log.error("[%d] socket error: %s", self.port, exc)


def load_events(capture: str | None):
    path = capture or replay.find_capture(CLIENT_DIR)
    if not path or not os.path.isfile(path):
        log.warning("no session capture found: the handshake will work but the server has nothing to replay")
        return []
    t = time.time()
    events = replay.load(path)
    log.info("loaded %d recorded messages (%d server, %d client) from %s in %.1fs", len(events),
             sum(1 for e in events if e.who == "S"), sum(1 for e in events if e.who == "C"),
             os.path.basename(path), time.time() - t)
    return events


async def serve(host: str, ports: list[int], capture: str | None = None,
                stop: asyncio.Event | None = None, ready: asyncio.Event | None = None,
                max_run: int = 10**9):
    """Bind every UDP port and run until `stop` is set (used by both the CLI and the AC2MOD UI)."""
    loop = asyncio.get_running_loop()
    server = Server(await loop.run_in_executor(None, load_events, capture), max_run)
    transports = []
    try:
        for port in ports:
            t, _ = await loop.create_datagram_endpoint(lambda port=port: AC2Protocol(server, port),
                                                       local_addr=(host, port))
            transports.append(t)
        if ready:
            ready.set()
        await (stop or asyncio.Event()).wait()
    finally:
        for t in transports:
            t.close()


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--ports", type=int, nargs="+", default=[7777, 7778])
    ap.add_argument("--capture", help="PacketLogger pcap to replay (default: the largest pkt_*_log.pcap in the client folder)")
    ap.add_argument("--max-run", type=int, default=10**9,
                    help="replay only the first N batches of recorded server messages (3 = stop before world entry)")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    await serve(args.host, args.ports, args.capture, max_run=args.max_run)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
