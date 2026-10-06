#!/usr/bin/env python3
"""Rebuild index.html, the project page whose hero is the Phase 7 climb chart, after a logged tuning run.

bench/quick log runs this after appending to bench/results/phase7-log.jsonl. The chart is bench/progress/climb.py;
the page around it is site/build.py, which this runs. (This used to write a separate dashboard under
target/phase7-dashboard/; the project page replaced it.) Nothing rebuilds it on a timer.
"""
import os
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SITE_BUILD = os.path.join(ROOT, "site/build.py")


def main():
    if not os.path.exists(SITE_BUILD):
        sys.stderr.write("site/build.py not found; nothing to rebuild\n")
        return
    try:
        r = subprocess.run([sys.executable, SITE_BUILD], capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.SubprocessError) as ex:
        sys.stderr.write("site/build.py did not run: %s\n" % ex)
        return
    sys.stdout.write(r.stdout)
    if r.returncode != 0:
        # The log line is already written; say so loudly, but don't fail the run that logged it.
        sys.stderr.write("site/build.py failed; index.html was not rebuilt:\n" + r.stderr[-2000:])


if __name__ == "__main__":
    main()
