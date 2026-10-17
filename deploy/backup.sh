#!/usr/bin/env bash
# Nightly backup of everything that cannot be rebuilt:  database, uploaded files, encryption keys.
# Run from cron as the sayserne user, e.g.:   30 2 * * *  /opt/sayserne/deploy/backup.sh
# Copy the result OFF this server (another machine or object storage): a backup on the same disk is not a backup.
set -euo pipefail
DEST="${BACKUP_DIR:-/var/backups/sayserne}"
STAMP="$(date +%Y%m%d-%H%M%S)"
KEEP_DAYS="${KEEP_DAYS:-14}"
mkdir -p "$DEST"

# 1. the database (the migrator account can read everything; the API's account cannot, on purpose)
mysqldump --single-transaction --routines --no-tablespaces \
  -h "${DB_HOST:-127.0.0.1}" -u "${DB_USER:?set DB_USER}" -p"${DB_PASSWORD:?set DB_PASSWORD}" "${DB_NAME:-sayserne}" | gzip > "$DEST/db-$STAMP.sql.gz"

# 2. uploaded photos and videos (only when Storage.Provider is "disk"; an S3 bucket has its own versioning/backup)
[ -d "${STORAGE_DIR:-/var/lib/sayserne/storage}" ] && tar -czf "$DEST/files-$STAMP.tar.gz" -C "$(dirname "${STORAGE_DIR:-/var/lib/sayserne/storage}")" "$(basename "${STORAGE_DIR:-/var/lib/sayserne/storage}")"

# 3. the Data Protection keys: without them every admin's two-factor secret and the stored e-mail password are unreadable
[ -d "${KEYS_DIR:-/var/lib/sayserne/dp-keys}" ] && tar -czf "$DEST/keys-$STAMP.tar.gz" -C "$(dirname "${KEYS_DIR:-/var/lib/sayserne/dp-keys}")" "$(basename "${KEYS_DIR:-/var/lib/sayserne/dp-keys}")" && chmod 600 "$DEST/keys-$STAMP.tar.gz"

find "$DEST" -type f -mtime +"$KEEP_DAYS" -delete
echo "Backup written to $DEST ($STAMP)"
