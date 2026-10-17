-- Repeatable: uploaded files and the background job queue.

DROP PROCEDURE IF EXISTS sp_files_create;
DROP PROCEDURE IF EXISTS sp_files_mark_ready;
DROP PROCEDURE IF EXISTS sp_files_mark_failed;
DROP PROCEDURE IF EXISTS sp_files_get;
DROP PROCEDURE IF EXISTS sp_files_viewer_allowed;
DROP PROCEDURE IF EXISTS sp_jobs_enqueue;
DROP PROCEDURE IF EXISTS sp_jobs_claim;
DROP PROCEDURE IF EXISTS sp_jobs_complete;
DROP PROCEDURE IF EXISTS sp_jobs_fail;
DROP PROCEDURE IF EXISTS sp_jobs_list;
DROP PROCEDURE IF EXISTS sp_jobs_stats;
DROP PROCEDURE IF EXISTS sp_jobs_retry;

DELIMITER $$

-- ------------------------------------------------------------------ files
CREATE PROCEDURE sp_files_create(
  IN p_id CHAR(32), IN p_owner_id BIGINT UNSIGNED, IN p_purpose VARCHAR(30), IN p_kind VARCHAR(10), IN p_original_name VARCHAR(255),
  IN p_original_bytes BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED, IN p_provider VARCHAR(20), IN p_incoming_key VARCHAR(300))
BEGIN
  IF NOT EXISTS (SELECT 1 FROM users WHERE id = p_owner_id AND is_locked = 0) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  IF p_room_id IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM chat_rooms WHERE id = p_room_id AND (user_a_id = p_owner_id OR user_b_id = p_owner_id)) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROOM_NOT_FOUND';
  END IF;
  INSERT INTO files (id, owner_id, purpose, media_kind, original_name, original_bytes, room_id, provider, incoming_key)
  VALUES (p_id, p_owner_id, p_purpose, p_kind, LEFT(p_original_name, 255), p_original_bytes, p_room_id, p_provider, p_incoming_key);
  CALL sp_files_get(p_id);
END$$

CREATE PROCEDURE sp_files_mark_ready(
  IN p_id CHAR(32), IN p_content_type VARCHAR(100), IN p_stored_bytes BIGINT UNSIGNED, IN p_width INT, IN p_height INT, IN p_duration_ms INT,
  IN p_provider VARCHAR(20), IN p_storage_key VARCHAR(300))
BEGIN
  UPDATE files SET status = 'ready', content_type = p_content_type, stored_bytes = p_stored_bytes, width = p_width, height = p_height,
         duration_ms = p_duration_ms, provider = p_provider, storage_key = p_storage_key, incoming_key = NULL, failure_code = NULL, ready_at = UTC_TIMESTAMP(3)
   WHERE id = p_id;
  IF ROW_COUNT() = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FILE_NOT_FOUND';
  END IF;
  CALL sp_files_get(p_id);
END$$

CREATE PROCEDURE sp_files_mark_failed(IN p_id CHAR(32), IN p_code VARCHAR(50))
BEGIN
  UPDATE files SET status = 'failed', failure_code = p_code WHERE id = p_id;
  CALL sp_files_get(p_id);
END$$

CREATE PROCEDURE sp_files_get(IN p_id CHAR(32))
BEGIN
  SELECT id, owner_id, purpose, media_kind, original_name, content_type, original_bytes, stored_bytes, width, height, duration_ms,
         status, failure_code, provider, storage_key, incoming_key, room_id, is_deleted, created_at, ready_at
  FROM files WHERE id = p_id;
END$$

-- May this viewer (NULL = anonymous) see the file? Profile, banner and ad images are public; chat media only to the two people in the room.
CREATE PROCEDURE sp_files_viewer_allowed(IN p_id CHAR(32), IN p_viewer_id BIGINT UNSIGNED)
BEGIN
  SELECT (f.is_deleted = 0 AND (
            f.purpose IN ('profile_photo', 'banner_photo', 'ad_image')
            -- chat media: only the two people in the room (and the uploader), and not once the message that carries it was deleted
            OR (NOT EXISTS (SELECT 1 FROM chat_messages dm WHERE dm.file_id = f.id AND dm.deleted_at IS NOT NULL)
                AND ((p_viewer_id IS NOT NULL AND f.owner_id = p_viewer_id)
                  OR (f.purpose = 'chat_media' AND p_viewer_id IS NOT NULL AND EXISTS (
                        SELECT 1 FROM chat_rooms r WHERE r.id = f.room_id AND (r.user_a_id = p_viewer_id OR r.user_b_id = p_viewer_id)))))
          )) AS allowed
  FROM files f WHERE f.id = p_id;
END$$

-- ------------------------------------------------------------------ jobs
-- dedupe_key makes "enqueue" idempotent: the same key never creates a second job (returns the existing one).
CREATE PROCEDURE sp_jobs_enqueue(IN p_type VARCHAR(60), IN p_payload JSON, IN p_run_at DATETIME(3), IN p_dedupe_key VARCHAR(150))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  INSERT INTO jobs (job_type, payload, run_at, max_attempts, dedupe_key)
  VALUES (p_type, p_payload, IFNULL(p_run_at, UTC_TIMESTAMP(3)), (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Jobs.MaxAttempts'), p_dedupe_key)
  ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id);
  SET v_id = LAST_INSERT_ID();
  SELECT id, job_type, payload, status, attempts, max_attempts, run_at, last_error, created_at, finished_at FROM jobs WHERE id = v_id;
END$$

-- Takes up to p_limit due jobs for this worker. A job whose lease expired (worker crashed) is taken over; one that has used all its attempts is failed instead.
CREATE PROCEDURE sp_jobs_claim(IN p_worker VARCHAR(100), IN p_limit INT, IN p_lease_seconds INT)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_ids VARCHAR(2000) DEFAULT '';
  DECLARE v_i INT DEFAULT 0;
  DECLARE v_now DATETIME(3);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SET v_now = UTC_TIMESTAMP(3);
  START TRANSACTION;
  UPDATE jobs SET status = 'failed', finished_at = v_now, last_error = 'The worker stopped repeatedly while running this job.'
   WHERE status = 'running' AND locked_until < v_now AND attempts >= max_attempts;
  claim: WHILE v_i < LEAST(GREATEST(IFNULL(p_limit, 1), 1), 50) DO
    SET v_id = NULL;
    SELECT id INTO v_id FROM jobs
     WHERE (status = 'pending' AND run_at <= v_now) OR (status = 'running' AND locked_until < v_now AND attempts < max_attempts)
     ORDER BY run_at, id LIMIT 1 FOR UPDATE SKIP LOCKED;
    IF v_id IS NULL THEN
      LEAVE claim;
    END IF;
    UPDATE jobs SET status = 'running', attempts = attempts + 1, locked_by = p_worker, locked_until = DATE_ADD(v_now, INTERVAL p_lease_seconds SECOND) WHERE id = v_id;
    SET v_ids = CONCAT_WS(',', NULLIF(v_ids, ''), v_id);
    SET v_i = v_i + 1;
  END WHILE;
  COMMIT;
  SELECT id, job_type, payload, status, attempts, max_attempts, run_at, last_error, created_at, finished_at FROM jobs WHERE FIND_IN_SET(id, v_ids) ORDER BY id;
END$$

CREATE PROCEDURE sp_jobs_complete(IN p_id BIGINT UNSIGNED, IN p_worker VARCHAR(100))
BEGIN
  UPDATE jobs SET status = 'done', finished_at = UTC_TIMESTAMP(3), locked_by = NULL, locked_until = NULL, last_error = NULL
   WHERE id = p_id AND status = 'running' AND locked_by = p_worker;
END$$

-- A failed attempt: retried after a delay that doubles each time (cap one day), unless the failure is permanent or attempts are used up.
-- p_error may be any length (exception messages and stack traces are long); it is cut to fit the column here, so recording a failure can never itself fail
CREATE PROCEDURE sp_jobs_fail(IN p_id BIGINT UNSIGNED, IN p_worker VARCHAR(100), IN p_error TEXT, IN p_permanent TINYINT(1))
BEGIN
  DECLARE v_attempts INT;
  DECLARE v_max INT;
  DECLARE v_delay INT;
  SELECT attempts, max_attempts INTO v_attempts, v_max FROM jobs WHERE id = p_id AND status = 'running' AND locked_by = p_worker FOR UPDATE;
  IF v_attempts IS NOT NULL THEN
    IF IFNULL(p_permanent, 0) = 1 OR v_attempts >= v_max THEN
      UPDATE jobs SET status = 'failed', finished_at = UTC_TIMESTAMP(3), locked_by = NULL, locked_until = NULL, last_error = LEFT(p_error, 1000) WHERE id = p_id;
    ELSE
      SET v_delay = LEAST((SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Jobs.RetryDelaySeconds') * POW(2, v_attempts - 1), 86400);
      UPDATE jobs SET status = 'pending', run_at = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL v_delay SECOND), locked_by = NULL, locked_until = NULL, last_error = LEFT(p_error, 1000) WHERE id = p_id;
    END IF;
  END IF;
END$$

CREATE PROCEDURE sp_jobs_list(IN p_status VARCHAR(10), IN p_type VARCHAR(60), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 25), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SELECT id, job_type, payload, status, attempts, max_attempts, run_at, last_error, created_at, finished_at
  FROM jobs WHERE (p_status IS NULL OR status = p_status) AND (p_type IS NULL OR job_type = p_type)
  ORDER BY id DESC LIMIT v_size OFFSET v_offset;
  SELECT COUNT(*) AS total FROM jobs WHERE (p_status IS NULL OR status = p_status) AND (p_type IS NULL OR job_type = p_type);
END$$

CREATE PROCEDURE sp_jobs_stats()
BEGIN
  SELECT job_type, status, COUNT(*) AS cnt FROM jobs GROUP BY job_type, status ORDER BY job_type, status;
END$$

CREATE PROCEDURE sp_jobs_retry(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_type VARCHAR(60);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  SELECT job_type INTO v_type FROM jobs WHERE id = p_id AND status = 'failed' FOR UPDATE;
  IF v_type IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'JOB_NOT_RETRYABLE';
  END IF;
  UPDATE jobs SET status = 'pending', attempts = 0, run_at = UTC_TIMESTAMP(3), finished_at = NULL, last_error = NULL WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'jobs.retry', 'job', p_id, CONCAT('Retried failed job ', p_id, ' (', v_type, ')'), NULL, NULL);
  COMMIT;
  SELECT id, job_type, payload, status, attempts, max_attempts, run_at, last_error, created_at, finished_at FROM jobs WHERE id = p_id;
END$$

DELIMITER ;
