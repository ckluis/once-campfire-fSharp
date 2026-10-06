#!/usr/bin/env python3
"""Drives a bench/cable server: N Action Cable clients on loopback, one broadcast stream.

  driver.py PORT PID [--clients 1000] [--deflate 0|1] [--bytes 600] [--rounds 30] [--burst 100] [--warmups 3]

Each client opens a WebSocket (offering permessage-deflate with --deflate 1, as browsers do), subscribes to
BenchChannel and reads. Then:

  latency   `rounds` broadcasts of one message each, a pause between: from the POST to the last client having
            the message (p50 and max over rounds; this includes the driver's own time to read N frames), and
            the server process's CPU time per delivered message over those rounds (a lone broadcast, which
            nothing batches)
  burst     one POST that broadcasts `burst` messages back to back (after `warmups` of them that aren't
            measured, so the JIT and the pools are warm, as in a server that has been up): from the POST to
            every client having the last one, and the server process's CPU time (ps) per delivered message,
            which doesn't depend on how fast this driver reads

It also reports the server's resident memory with the clients connected and idle, and the CPU that idling costs
(the heartbeat ping to every client every 3 seconds is most of it). One JSON line on stdout.
"""
import argparse
import asyncio
import base64
import json
import os
import statistics
import subprocess
import time
import urllib.request
import zlib

TAIL = b"\x00\x00\xff\xff"


def clock(text):
    seconds = 0.0
    for part in text.split(":"):
        seconds = seconds * 60 + float(part)
    return seconds


def cpu_seconds(pid):
    """The process's user and system CPU seconds."""
    out = subprocess.run(["ps", "-o", "utime=,stime=", "-p", str(pid)], capture_output=True, text=True).stdout.split()
    return clock(out[0]), clock(out[1])


def rss_mb(pid):
    out = subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
    return int(out) / 1024


class Round:
    def __init__(self, clients):
        self.clients = clients
        self.seen = {}
        self.events = {}

    def event(self, seq):
        return self.events.setdefault(seq, asyncio.Event())

    def delivered(self, seq):
        count = self.seen.get(seq, 0) + 1
        self.seen[seq] = count
        if count == self.clients:
            self.event(seq).set()


async def client(port, deflate, tracker, ready, started):
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    key = base64.b64encode(os.urandom(16)).decode()
    request = (
        f"GET /cable HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: http://127.0.0.1:{port}\r\n"
        "Sec-WebSocket-Protocol: actioncable-v1-json\r\nCookie: bench=1\r\n"
    )
    if deflate:
        request += "Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits\r\n"
    writer.write((request + "\r\n").encode())
    head = await reader.readuntil(b"\r\n\r\n")
    assert head.startswith(b"HTTP/1.1 101"), head
    compressed = deflate and b"permessage-deflate" in head.lower()
    identifier = json.dumps({"channel": "BenchChannel"})
    command = json.dumps({"command": "subscribe", "identifier": identifier}).encode()
    mask = os.urandom(4)
    frame = bytearray([0x81, 0x80 | len(command)]) if len(command) < 126 else bytearray([0x81, 0xFE]) + len(command).to_bytes(2, "big")
    frame += mask + bytes(b ^ mask[i % 4] for i, b in enumerate(command))
    writer.write(bytes(frame))
    started.append(1)
    marker = b'data-seq=\\"'
    while True:
        b0, b1 = await reader.readexactly(2)
        length = b1 & 0x7F
        if length == 126:
            length = int.from_bytes(await reader.readexactly(2), "big")
        elif length == 127:
            length = int.from_bytes(await reader.readexactly(8), "big")
        payload = await reader.readexactly(length)
        if b0 & 0x0F != 1:
            continue
        if b0 & 0x40:
            payload = zlib.decompressobj(-15).decompress(payload + TAIL)
        if payload.startswith(b'{"identifier"') and b"confirm_subscription" in payload:
            ready.append(1)
            continue
        at = payload.find(marker)
        if at >= 0:
            start = at + len(marker)
            end = payload.index(b"\\", start)
            tracker.delivered(int(payload[start:end]))


def post(port, seq, size, count):
    url = f"http://127.0.0.1:{port}/broadcast?seq={seq}&bytes={size}&count={count}"
    with urllib.request.urlopen(urllib.request.Request(url, method="POST", data=b"")) as response:
        return int(response.read())


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("port", type=int)
    parser.add_argument("pid", type=int)
    parser.add_argument("--clients", type=int, default=1000)
    parser.add_argument("--deflate", type=int, default=0)
    parser.add_argument("--bytes", type=int, default=600)
    parser.add_argument("--rounds", type=int, default=30)
    parser.add_argument("--burst", type=int, default=100)
    parser.add_argument("--idle", type=float, default=6.0, help="seconds to leave the connected clients idle, to see what that costs")
    parser.add_argument("--warmups", type=int, default=3, help="bursts before the measured one, for the JIT and the pools")
    args = parser.parse_args()

    tracker = Round(args.clients)
    ready, started = [], []
    rss_before = rss_mb(args.pid)
    tasks = []
    for i in range(args.clients):
        tasks.append(asyncio.create_task(client(args.port, args.deflate, tracker, ready, started)))
        if i % 50 == 49:
            await asyncio.sleep(0.05)
    deadline = time.time() + 60
    while len(ready) < args.clients and time.time() < deadline:
        await asyncio.sleep(0.05)
    assert len(ready) == args.clients, f"{len(ready)} of {args.clients} subscribed"
    await asyncio.sleep(1.0)
    rss_idle = rss_mb(args.pid)
    idle_user0, idle_system0 = cpu_seconds(args.pid)
    await asyncio.sleep(args.idle)
    idle_user1, idle_system1 = cpu_seconds(args.pid)

    loop = asyncio.get_running_loop()
    latencies, publish = [], []
    seq = 1
    # Rounds before the measured ones, so the JIT and the pools are warm, as in a server that has been up.
    for _ in range(args.warmups * 10):
        event = tracker.event(seq)
        await loop.run_in_executor(None, post, args.port, seq, args.bytes, 1)
        await asyncio.wait_for(event.wait(), 30)
        seq += 1
        await asyncio.sleep(0.02)
    single_user0, single_system0 = cpu_seconds(args.pid)
    for _ in range(args.rounds):
        event = tracker.event(seq)
        t0 = time.perf_counter()
        received = await loop.run_in_executor(None, post, args.port, seq, args.bytes, 1)
        publish.append(time.perf_counter() - t0)
        assert received == args.clients, received
        await asyncio.wait_for(event.wait(), 30)
        latencies.append(time.perf_counter() - t0)
        seq += 1
        await asyncio.sleep(0.05)

    for _ in range(args.warmups):
        last = seq + args.burst - 1
        event = tracker.event(last)
        await loop.run_in_executor(None, post, args.port, seq, args.bytes, args.burst)
        await asyncio.wait_for(event.wait(), 120)
        seq = last + 1
        await asyncio.sleep(0.3)

    single_user1, single_system1 = cpu_seconds(args.pid)
    user0, system0 = cpu_seconds(args.pid)
    first, last = seq, seq + args.burst - 1
    event = tracker.event(last)
    t0 = time.perf_counter()
    await loop.run_in_executor(None, post, args.port, first, args.bytes, args.burst)
    await asyncio.wait_for(event.wait(), 120)
    burst_seconds = time.perf_counter() - t0
    await asyncio.sleep(0.5)
    user1, system1 = cpu_seconds(args.pid)
    deliveries = args.clients * args.burst
    rss_after = rss_mb(args.pid)

    print(
        json.dumps(
            {
                "clients": args.clients,
                "deflate": bool(args.deflate),
                "bytes": args.bytes,
                "rss_mb_before": round(rss_before, 1),
                "rss_mb_idle": round(rss_idle, 1),
                "rss_kb_per_client": round((rss_idle - rss_before) * 1024 / args.clients, 1),
                "rss_mb_after_burst": round(rss_after, 1),
                "idle_cpu_ms_per_second": round((idle_user1 - idle_user0 + idle_system1 - idle_system0) * 1000 / args.idle, 1),
                "publish_ms_p50": round(statistics.median(publish) * 1000, 2),
                "latency_ms_p50": round(statistics.median(latencies) * 1000, 1),
                "latency_ms_max": round(max(latencies) * 1000, 1),
                "single_cpu_us_per_delivery": round(
                    (single_user1 - single_user0 + single_system1 - single_system0) * 1e6 / (args.clients * args.rounds), 2
                ),
                "burst_seconds": round(burst_seconds, 3),
                "burst_deliveries": deliveries,
                "server_cpu_us_per_delivery": round((user1 - user0 + system1 - system0) * 1e6 / deliveries, 2),
                "server_user_us_per_delivery": round((user1 - user0) * 1e6 / deliveries, 2),
                "server_sys_us_per_delivery": round((system1 - system0) * 1e6 / deliveries, 2),
            }
        )
    )
    for task in tasks:
        task.cancel()


asyncio.run(main())
