#!/usr/bin/env bash
# Phase 7 final review: room page and post a message for one app, measured by hand (not through bench/run's loop):
# fixed 90 s warm-up at the measured concurrency, a 15 s window, the cgroup's cpu.stat read directly, requests counted by the
# load generator AND by the server's own request-log lines, rows counted in SQLite, load average and host probe around each window.
#   target/review/manual.sh APP   (rust|fsharp), inside colima
set -euo pipefail
cd /Users/clank/Desktop/projects/once-campfire-fsharp
APP=$1 PORT=4395 NAME=review-$1
IMAGE=$([ "$APP" = rust ] && echo campfire-rust:app || echo campfire-fsharp:app)
LG=target/rust/aarch64-unknown-linux-gnu/release/loadgen
W=/var/tmp/campfire-review/$APP
rm -rf "$W"; mkdir -p "$W/db" "$W/storage"
cp -a parity/.seed/default/db/. "$W/db/"; cp -a parity/.seed/default/storage/. "$W/storage/"
python3 - "$W/db/production.sqlite3" <<'PY'
import sqlite3, sys
db = sqlite3.connect(sys.argv[1])
db.execute("UPDATE push_subscriptions SET endpoint = 'https://127.0.0.1:9/push/' || id")
db.execute("UPDATE webhooks SET url = 'http://127.0.0.1:9/hook/' || id")
db.commit()
PY
args=(); while IFS= read -r l; do args+=("$l"); done < <(grep -Ev '^(#|$|WEB_CONCURRENCY|JOB_CONCURRENCY|RAILS_MAX_THREADS|RAILS_LOG_LEVEL)' parity/.env.reference | sed 's/^/-e\n/')
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" --cpuset-cpus 0-3 --user "$(id -u):$(id -g)" --log-driver json-file --network host \
  -e HTTP_PORT=$PORT -e TARGET_PORT=$((PORT + 1)) "${args[@]}" -e WEB_CONCURRENCY=3 -e JOB_CONCURRENCY=3 -e RAILS_MAX_THREADS=5 -e RAILS_LOG_LEVEL=warn \
  -v "$W/db:/rails/storage/db" -v "$W/storage:/rails/storage/files" "$IMAGE" >/dev/null
for _ in $(seq 1 500); do curl -fsS -o /dev/null http://127.0.0.1:$PORT/up 2>/dev/null && break; sleep 0.02; done
echo "image: $(docker inspect -f '{{.Image}}' "$NAME") revision: $(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$IMAGE")"
CG=/sys/fs/cgroup/docker/$(docker inspect -f '{{.Id}}' "$NAME"); [ -d "$CG" ] || CG=/sys/fs/cgroup/system.slice/docker-$(docker inspect -f '{{.Id}}' "$NAME").scope
L=$(python3 -c 'import json;print(json.dumps(json.load(open("parity/.seed/default/labels.json"))))')
lab() { python3 -c 'import json,sys;print(json.loads(sys.argv[1])[sys.argv[2]])' "$L" "$1"; }
ROOM=$(lab rooms.watercooler) WROOM=$(lab rooms.hq)
COOKIE=$($LG login --base http://127.0.0.1:$PORT --email "$(lab emails.david)" --password "$(lab passwords.all)" | python3 -c 'import json,sys;print(json.load(sys.stdin)["cookie"])')
CSRF=$($LG scrape --base http://127.0.0.1:$PORT --cookie "$COOKIE" --room "$ROOM" | python3 -c 'import json,sys;print(json.load(sys.stdin)["csrf"] or "")')
rows() { python3 -c 'import sqlite3,sys;print(sqlite3.connect(sys.argv[1],timeout=30).execute("select count(*) from messages where room_id=?",(int(sys.argv[2]),)).fetchone()[0])' "$W/db/production.sqlite3" "$WROOM"; }
loglines() { docker logs --since "$1" --until "$2" "$NAME" 2>&1 | grep -c 'Request.*path' || true; }
usage() { awk '$1=="usage_usec"{print $2}' "$CG/cpu.stat"; }
measure() {
  local name=$1 c=$2 warm=$3; shift 3
  timeout $((warm + 5)) taskset -c 4-7 $LG http --base http://127.0.0.1:$PORT --cookie "$COOKIE" "$@" --conc "$c" --duration "$warm" > /dev/null
  sleep 1
  local r0 l0 u0 t0 out r1 l1 u1 t1
  r0=$(rows); u0=$(usage); t0=$(date +%s.%N)
  out=$(taskset -c 4-7 $LG http --base http://127.0.0.1:$PORT --cookie "$COOKIE" "$@" --conc "$c" --duration 15)
  u1=$(usage); t1=$(date +%s.%N); sleep 1; l0=0; l1=$(loglines "$t0" "$t1"); r1=$(rows)
  python3 - "$APP" "$name" "$c" "$out" "$u0" "$u1" "$t0" "$t1" "$l0" "$l1" "$r0" "$r1" "$(cut -d' ' -f1-3 /proc/loadavg)" "$(python3 bench/lib/hostprobe.py 0-3,4-7 /var/tmp/campfire-bench-work/.hostprobe-baseline)" <<'PY'
import json, sys
app, name, c, out, u0, u1, t0, t1, l0, l1, r0, r1, load, probe = sys.argv[1:]
o = json.loads(out); n = o["latency"]["n"]; wall = float(t1) - float(t0)
print(json.dumps({"app": app, "route": name, "c": int(c), "rps_loadgen": o["rps"], "n_loadgen": n, "statuses": o["statuses"], "errors": o["errors"],
  "server_log_lines": int(l1) - int(l0), "rows_written": int(r1) - int(r0), "cpu_us_per_req": round((int(u1) - int(u0)) / n, 1),
  "cores_busy": round((int(u1) - int(u0)) / 1e6 / wall, 2), "bytes_per_req": o.get("bytes_per_req", o.get("avg_bytes")), "p50": o["latency"].get("p50_ms"), "p99": o["latency"].get("p99_ms"),
  "loadavg_after": load, "host_probe_after": json.loads(probe)["ratio"]}))
PY
}
measure room_show 16 90 --path "/rooms/$ROOM"
measure room_show 1 45 --path "/rooms/$ROOM"
measure post_message 16 90 --post-room "$WROOM" --csrf "$CSRF"
measure post_message 1 45 --post-room "$WROOM" --csrf "$CSRF"
docker rm -f "$NAME" >/dev/null; rm -rf "$W"
