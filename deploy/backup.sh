#!/bin/sh
# Dumb Luck Rides — nightly backup (§9, §9.1).
#
# pg_dump plus the blob volume, through restic, to Backblaze B2. Two properties matter more than
# the schedule:
#
#   Encrypted client-side. restic encrypts with RESTIC_PASSWORD before anything leaves this
#   machine, so a storage-provider breach is not a user-data breach — which matters more here than
#   usual, because backups contain last-known positions and email addresses (§10.1).
#
#   Off-provider. Hetzner's own Storage Box is cheaper and faster and is the same account as the
#   server: fine as a second copy, never as the only one.
#
# And the part that is not automatable: *run a restore drill*. Restore egress from B2 is free up to
# three times the stored volume, so a drill costs nothing — which removes the only excuse. A backup
# you have never restored is a hope.

set -eu

apk add --no-cache --quiet postgresql17-client restic

: "${RESTIC_REPOSITORY:?}"
: "${RESTIC_PASSWORD:?}"
: "${BACKUP_HOUR_UTC:=17}"

DUMP_DIR=/tmp/dump

# `init` on an existing repository is an error rather than a no-op, so the failure is swallowed
# only here and only for that reason. Any other restic failure below is fatal.
restic snapshots >/dev/null 2>&1 || restic init

run_backup() {
	echo "$(date -u +%FT%TZ) starting backup"

	mkdir -p "$DUMP_DIR"

	# Custom format, not plain SQL: it restores selectively and compresses, and pg_restore can
	# rebuild indexes in parallel. --clean --if-exists makes the restore idempotent, which is
	# what turns a drill into something you can run twice without tidying up first.
	pg_dump --format=custom --clean --if-exists --file="$DUMP_DIR/dlr.dump"

	# One snapshot covering both, so a restore never pairs a database with blobs from another
	# night. A track row pointing at a blob that the other snapshot does not have is a broken
	# ride nobody can explain.
	restic backup --tag nightly "$DUMP_DIR/dlr.dump" /blobs

	rm -f "$DUMP_DIR/dlr.dump"

	# Kept: a fortnight of days, then weeks, then a year of months. §15.6 already says out loud
	# that a trimmed track's original survives in backups until it rolls out of retention — this
	# is the number that sentence refers to, so changing it changes a privacy statement.
	restic forget --tag nightly --keep-daily 14 --keep-weekly 8 --keep-monthly 12 --prune

	# Cheap and worth it. A repository that has silently rotted is indistinguishable from a good
	# one until the night you need it.
	restic check --read-data-subset=1%

	echo "$(date -u +%FT%TZ) backup complete"
}

# A sleep loop rather than cron, so there is one process, one log stream and no second scheduler
# to configure inside a container. Woken hourly; acts once a day.
while true; do
	if [ "$(date -u +%H)" = "$(printf '%02d' "$BACKUP_HOUR_UTC")" ]; then
		# Failures are loud and non-fatal: a backup that could not reach B2 tonight must not stop
		# the one that could tomorrow.
		run_backup || echo "$(date -u +%FT%TZ) BACKUP FAILED" >&2

		# Past the hour, so a run that finishes inside it does not immediately start again.
		sleep 3600
	fi

	sleep 600
done
