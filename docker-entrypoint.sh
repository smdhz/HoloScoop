#!/bin/sh
set -eu

# Docker named volumes start as root-owned. The process itself still runs as app.
chown app:app /data/work
if [ "${INITIALIZE_LIBRARY_PERMISSIONS:-false}" = "true" ]; then
    chown app:app /data/library
fi

exec setpriv --reuid=app --regid=app --init-groups "$@"
