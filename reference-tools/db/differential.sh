#!/usr/bin/env bash
# The reference app's side of Campfire.Db's differential tests. Run inside the colima VM (it needs
# Docker); `bin/db-differential` drives all of it from the host, where the .NET SDK is.
#
#   differential.sh reference   db:prepare, db:fixtures:load and scenario.rb in the reference image,
#                               leaving the databases in OUT (default target/db-differential):
#                               schema_ruby.sql, fixtures_ruby.sqlite3, scenario_ruby.sqlite3
#   differential.sh rollback    the reference boots on fsharp_export.sqlite3, a database Campfire.Db wrote
#                               (the `export database for rails` test), and reads, edits, searches and
#                               deletes through Active Record (ruby/rollback.rb)
#
# Between the two the host runs the F# tests that compare with those databases:
#
#   CAMPFIRE_RUBY_FIXTURES_DB=target/db-differential/fixtures_ruby.sqlite3   fixtures match ruby row for row
#   CAMPFIRE_RUBY_SCENARIO_DB=target/db-differential/scenario_ruby.sqlite3   scenario matches ruby
#   CAMPFIRE_EXPORT_DB=target/db-differential/fsharp_export.sqlite3            export database for rails
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${OUT:-$ROOT/target/db-differential}
IMAGE=${REFERENCE_IMAGE:-campfire-reference:latest}
mkdir -p "$OUT"

# sqlite_master minus what SQLite derives on its own (see src/Campfire.Db/Schema.fs).
SCHEMA_QUERY="SELECT sql || ';' FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'message_search_index_%' ORDER BY rowid"

reference() {
  docker run --rm --entrypoint "" \
    --cpus "${PARITY_CPUS:-2}" \
    --user "$(id -u):$(id -g)" \
    --env-file "$ROOT/parity/.env.reference" \
    -e RAILS_ENV=test -e RAILS_LOG_LEVEL=warn -e SCHEMA_QUERY="$SCHEMA_QUERY" \
    -v "$ROOT/reference-tools/db/ruby:/tools:ro" -v "$OUT:/out" \
    "$IMAGE" sh -ec "$1" 2> >(grep -v -e VIPS -e '^$' >&2)
}

case "${1:-}" in
  reference)
    rm -f "$OUT"/schema_ruby.sql "$OUT"/fixtures_ruby.sqlite3 "$OUT"/scenario_ruby.sqlite3
    echo "== reference: db:prepare, db:fixtures:load, scenario.rb"
    reference '
      db=storage/db/test.sqlite3
      bin/rails db:prepare >/dev/null
      sqlite3 $db "$SCHEMA_QUERY" > /out/schema_ruby.sql
      bin/rails db:fixtures:load
      sqlite3 $db "PRAGMA wal_checkpoint(TRUNCATE)" >/dev/null && cp $db /out/fixtures_ruby.sqlite3
      bin/rails runner /tools/scenario.rb
      sqlite3 $db "PRAGMA wal_checkpoint(TRUNCATE)" >/dev/null && cp $db /out/scenario_ruby.sqlite3'
    ;;
  rollback)
    echo "== reference on the F#-written database"
    reference '
      sqlite3 /out/fsharp_export.sqlite3 "PRAGMA wal_checkpoint(TRUNCATE)" >/dev/null
      cp /out/fsharp_export.sqlite3 storage/db/test.sqlite3
      bin/rails runner /tools/rollback.rb'
    ;;
  *)
    echo "usage: $0 reference|rollback" >&2
    exit 2
    ;;
esac
