#!/bin/sh
set -eu

# Docker named volumes start as root-owned. The process itself still runs as app.
chown app:app /data/work

exec setpriv --reuid=app --regid=app --init-groups "$@"
