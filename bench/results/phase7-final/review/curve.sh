#!/usr/bin/env bash
# 2 s windows of room page at c=16 for N windows on a fresh container: rps, CPU us/req, anon MB, threads, per window.
set -euo pipefail
cd /Users/clank/Desktop/projects/once-campfire-fsharp
APP=$1 ROUTE=${2:-room_show} N=${3:-75} PORT=4396 NAME=curve-$1
IMAGE=$([ "$APP" = rust ] && echo campfire-rust:app || echo campfire-fsharp:app)
LG=target/rust/aarch64-unknown-linux-gnu/release/loadgen
W=/var/tmp/campfire-review/curve-$APP
rm -rf "$W"; mkdir -p "$W/db" "$W/storage"
cp -a parity/.seed/default/db/. "$W/db/"; cp -a parity/.seed/default/storage/. "$W/storage/"
args=(); while IFS= read -r l; do args+=("$l"); done < <(grep -Ev '^(#|$|WEB_CONCURRENCY|JOB_CONCURRENCY|RAILS_MAX_THREADS|RAILS_LOG_LEVEL)' parity/.env.reference | sed 's/^/-e\n/')
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" --cpuset-cpus 0-3 --user "$(id -u):$(id -g)" --log-driver json-file --log-opt max-size=10m --log-opt max-file=2 --network host \
  -e HTTP_PORT=$PORT -e TARGET_PORT=$((PORT + 1)) "${args[@]}" -e WEB_CONCURRENCY=3 -e JOB_CONCURRENCY=3 -e RAILS_MAX_THREADS=5 -e RAILS_LOG_LEVEL=warn \
  -v "$W/db:/rails/storage/db" -v "$W/storage:/rails/storage/files" "$IMAGE" >/dev/null
for _ in $(seq 1 500); do curl -fsS -o /dev/null http://127.0.0.1:$PORT/up 2>/dev/null && break; sleep 0.02; done
CG=/sys/fs/cgroup/docker/$(docker inspect -f '{{.Id}}' "$NAME")
lab() { python3 -c 'import json,sys;print(json.load(open("parity/.seed/default/labels.json"))[sys.argv[1]])' "$1"; }
ROOM=$(lab rooms.watercooler)
COOKIE=$($LG login --base http://127.0.0.1:$PORT --email "$(lab emails.david)" --password "$(lab passwords.all)" | python3 -c 'import json,sys;print(json.load(sys.stdin)["cookie"])')
case $ROUTE in room_show) P="/rooms/$ROOM";; sidebar) P=/users/me/sidebar;; search) P="/searches?q=coffee";; messages_page) P="/rooms/$ROOM/messages?before=$(lab messages.busy_060)";; esac
PID=$(docker inspect -f '{{.State.Pid}}' "$NAME")
for i in $(seq 1 "$N"); do
  u0=$(awk '$1=="usage_usec"{print $2}' $CG/cpu.stat)
  o=$(taskset -c 4-7 $LG http --base http://127.0.0.1:$PORT --cookie "$COOKIE" --path "$P" --conc 16 --duration 2)
  u1=$(awk '$1=="usage_usec"{print $2}' $CG/cpu.stat)
  anon=$(awk '$1=="anon"{print int($2/1048576)}' $CG/memory.stat)
  thr=$(ls /proc/$PID/task 2>/dev/null | wc -l)
  python3 -c 'import json,sys;o=json.loads(sys.argv[1]);n=o["latency"]["n"];print(sys.argv[2],round(o["rps"]),round((int(sys.argv[4])-int(sys.argv[3]))/n,1),sys.argv[5],sys.argv[6],o["latency"]["p99_ms"])' "$o" "$((i*2))" "$u0" "$u1" "$anon" "$thr"
done | awk '{printf "%ss rps=%s cpu=%s anon=%sMB thr=%s p99=%s\n",$1,$2,$3,$4,$5,$6}'
docker rm -f "$NAME" >/dev/null; rm -rf "$W"
