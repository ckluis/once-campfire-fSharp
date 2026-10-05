#!/bin/sh
# reset-request.sh CTRL PORT TARGET URL: asks the host's reset loop (run.sh start_reset_loop) to
# restore the server on PORT, and waits for it. Runs inside the capture image.
set -eu
ctrl=$1 port=$2 target=$3 url=$4
id=$$.$(date +%s%N)
echo "$port $target $url" >"$ctrl/.tmp.$id"
mv "$ctrl/.tmp.$id" "$ctrl/req.$id"
while [ ! -e "$ctrl/done.$id" ]; do sleep 0.1; done
status=$(cat "$ctrl/done.$id"); rm -f "$ctrl/done.$id"
[ "$status" = ok ] || { echo "reset of $url failed" >&2; exit 1; }
