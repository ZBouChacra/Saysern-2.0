-- V009: uploaded files (images and videos) and the background job queue.

-- One row per uploaded file. The bytes live in the storage provider named here (disk or S3-compatible), so switching the
-- active provider later never strands old files.
CREATE TABLE files (
  id             CHAR(32)     NOT NULL,
  owner_id       BIGINT UNSIGNED NOT NULL,
  purpose        VARCHAR(30)  NOT NULL,
  media_kind     ENUM('image','video') NOT NULL,
  original_name  VARCHAR(255) NULL,
  content_type   VARCHAR(100) NULL,
  original_bytes BIGINT UNSIGNED NOT NULL,
  stored_bytes   BIGINT UNSIGNED NULL,
  width          INT          NULL,
  height         INT          NULL,
  duration_ms    INT          NULL,
  status         ENUM('processing','ready','failed') NOT NULL DEFAULT 'processing',
  failure_code   VARCHAR(50)  NULL,
  provider       VARCHAR(20)  NULL,
  storage_key    VARCHAR(300) NULL,
  incoming_key   VARCHAR(300) NULL,
  room_id        BIGINT UNSIGNED NULL,
  is_deleted     TINYINT(1)   NOT NULL DEFAULT 0,
  created_at     DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  ready_at       DATETIME(3)  NULL,
  PRIMARY KEY (id),
  KEY ix_files_owner (owner_id, purpose),
  KEY ix_files_room (room_id),
  CONSTRAINT fk_files_owner FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE CASCADE,
  CONSTRAINT fk_files_room  FOREIGN KEY (room_id)  REFERENCES chat_rooms (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Work that must not happen inside a web request (compressing a video, sending e-mail). Workers claim rows with
-- FOR UPDATE SKIP LOCKED, so any number of API instances can run workers without doing a job twice.
CREATE TABLE jobs (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  job_type     VARCHAR(60)  NOT NULL,
  payload      JSON         NULL,
  status       ENUM('pending','running','done','failed') NOT NULL DEFAULT 'pending',
  attempts     INT          NOT NULL DEFAULT 0,
  max_attempts INT          NOT NULL,
  run_at       DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  locked_by    VARCHAR(100) NULL,
  locked_until DATETIME(3)  NULL,
  last_error   VARCHAR(1000) NULL,
  dedupe_key   VARCHAR(150) NULL,
  created_at   DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  finished_at  DATETIME(3)  NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_jobs_dedupe (dedupe_key),
  KEY ix_jobs_claim (status, run_at),
  KEY ix_jobs_type (job_type, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE chat_messages
  MODIFY message_type ENUM('text','location','image','video') NOT NULL DEFAULT 'text',
  ADD COLUMN file_id CHAR(32) NULL,
  ADD KEY ix_chat_messages_file (file_id);

-- settings, permissions and the list of storage providers
INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Storage.Provider',        'Media', 'string', 'disk', 'disk', NULL, NULL, 0, 'Where NEW uploads are stored: "disk" (this server) or "s3" (an S3-compatible object store). Existing files stay where they are. Connection details are deploy-time configuration.'),
 ('Media.VideoEnabled',      'Media', 'bool',   'true', 'true', NULL, NULL, 1, 'Allow video uploads (turn off to save space).'),
 ('Media.MaxImageMB',        'Media', 'int',    '5',    '5',    1,    50,   1, 'An image is stored at no more than this size (larger uploads are resized and recompressed to fit).'),
 ('Media.MaxVideoMB',        'Media', 'int',    '25',   '25',   1,    500,  1, 'A video is stored at no more than this size (larger uploads are compressed to fit).'),
 ('Media.MaxUploadMB',       'Media', 'int',    '100',  '100',  5,    1000, 1, 'The largest file a user may upload before compression. Bigger uploads are refused immediately.'),
 ('Media.ImageMaxDimension', 'Media', 'int',    '1600', '1600', 320,  4000, 0, 'Images are scaled down so neither side exceeds this many pixels (never enlarged).'),
 ('Media.VideoMaxHeight',    'Media', 'int',    '720',  '720',  240,  1080, 0, 'Videos are scaled down to at most this height in pixels (never enlarged).'),
 ('Media.VideoMaxSeconds',   'Media', 'int',    '120',  '120',  5,    900,  1, 'Longest video accepted.'),
 ('Jobs.MaxAttempts',        'Background jobs', 'int', '5',   '5',   1,  20,   0, 'How many times a failing background job is tried before it is marked failed.'),
 ('Jobs.RetryDelaySeconds',  'Background jobs', 'int', '30',  '30',  5,  3600, 0, 'Delay before the first retry of a failed job; it doubles each time.'),
 ('Jobs.LeaseSeconds',       'Background jobs', 'int', '900', '900', 60, 7200, 0, 'How long a worker may hold a job before another worker may take it over (a crashed worker releases its jobs this way).');
INSERT INTO permissions (code, group_name, description) VALUES
 ('jobs.read',   'Operations', 'See background jobs and their errors.'),
 ('jobs.manage', 'Operations', 'Retry failed background jobs.');
INSERT INTO lookup_types (code, name, description, is_system) VALUES ('storage_provider', 'Storage providers', 'Places uploaded files can be kept.', 1);
INSERT INTO lookup_values (type_code, code, sort_order) VALUES ('storage_provider', 'disk', 1), ('storage_provider', 's3', 2);
INSERT INTO lookup_translations (value_id, language, name)
  SELECT id, 'en', IF(code = 'disk', 'This server''s disk', 'S3-compatible object storage') FROM lookup_values WHERE type_code = 'storage_provider';
INSERT INTO lookup_translations (value_id, language, name)
  SELECT id, 'ar', IF(code = 'disk', 'قرص هذا الخادم', 'تخزين كائنات متوافق مع S3') FROM lookup_values WHERE type_code = 'storage_provider';
