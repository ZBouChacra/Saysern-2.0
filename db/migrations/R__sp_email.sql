-- Repeatable: tokens, e-mail outbox and notification preferences.

DROP PROCEDURE IF EXISTS sp_tokens_create;
DROP PROCEDURE IF EXISTS sp_email_verify;
DROP PROCEDURE IF EXISTS sp_password_reset;
DROP PROCEDURE IF EXISTS sp_notify_prepare;
DROP PROCEDURE IF EXISTS sp_notification_prefs_list;
DROP PROCEDURE IF EXISTS sp_notification_prefs_set;
DROP PROCEDURE IF EXISTS sp_email_outbox_create;
DROP PROCEDURE IF EXISTS sp_email_outbox_get;
DROP PROCEDURE IF EXISTS sp_email_outbox_mark;
DROP PROCEDURE IF EXISTS sp_email_outbox_list;
DROP PROCEDURE IF EXISTS sp_chat_room_unread;

DELIMITER $$

-- Issues a token (only its hash is stored). Older unused tokens of the same kind stop working. A limit per hour stops mail-bombing.
CREATE PROCEDURE sp_tokens_create(IN p_user_id BIGINT UNSIGNED, IN p_purpose VARCHAR(20), IN p_token_hash CHAR(64), IN p_ttl_minutes INT)
BEGIN
  DECLARE v_recent INT;
  DECLARE v_expires DATETIME(3);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  IF NOT EXISTS (SELECT 1 FROM users WHERE id = p_user_id AND is_locked = 0 FOR UPDATE) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  SELECT COUNT(*) INTO v_recent FROM user_tokens
   WHERE user_id = p_user_id AND purpose = p_purpose AND created_at > DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 HOUR);
  IF v_recent >= (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Email.MaxTokenEmailsPerHour') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'TOO_MANY_EMAILS';
  END IF;
  UPDATE user_tokens SET used_at = UTC_TIMESTAMP(3) WHERE user_id = p_user_id AND purpose = p_purpose AND used_at IS NULL;
  SET v_expires = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL p_ttl_minutes MINUTE);
  INSERT INTO user_tokens (user_id, purpose, token_hash, expires_at) VALUES (p_user_id, p_purpose, p_token_hash, v_expires);
  COMMIT;
  SELECT v_expires AS expires_at;
END$$

-- Consumes a verification token: single use, must be unexpired. Marks the account's address as verified.
CREATE PROCEDURE sp_email_verify(IN p_token_hash CHAR(64))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_user BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  SELECT id, user_id INTO v_id, v_user FROM user_tokens
   WHERE token_hash = p_token_hash AND purpose = 'verify_email' AND used_at IS NULL AND expires_at > UTC_TIMESTAMP(3) FOR UPDATE;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TOKEN';
  END IF;
  UPDATE user_tokens SET used_at = UTC_TIMESTAMP(3) WHERE id = v_id;
  UPDATE users SET email_verified = 1 WHERE id = v_user;
  COMMIT;
  SELECT v_user AS user_id;
END$$

-- Consumes a reset token and sets the new password. Proving control of the mailbox also verifies the address and clears a temporary lockout.
-- An admin lock is NOT lifted. The caller has already checked the password against the policy and hashed it.
CREATE PROCEDURE sp_password_reset(IN p_token_hash CHAR(64), IN p_password_hash VARCHAR(255))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_user BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  SELECT id, user_id INTO v_id, v_user FROM user_tokens
   WHERE token_hash = p_token_hash AND purpose = 'reset_password' AND used_at IS NULL AND expires_at > UTC_TIMESTAMP(3) FOR UPDATE;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TOKEN';
  END IF;
  UPDATE user_tokens SET used_at = UTC_TIMESTAMP(3) WHERE user_id = v_user AND purpose = 'reset_password' AND used_at IS NULL;
  UPDATE users SET password_hash = p_password_hash, email_verified = 1, failed_login_attempts = 0, locked_until = NULL WHERE id = v_user;
  COMMIT;
  SELECT v_user AS user_id;
END$$

-- Should this user get this event on this channel, and in which language? Unverified addresses only ever receive the mandatory events
-- (so nobody can be mail-bombed by someone registering with their address).
CREATE PROCEDURE sp_notify_prepare(IN p_user_id BIGINT UNSIGNED, IN p_event VARCHAR(60), IN p_channel VARCHAR(20))
BEGIN
  SELECT u.id, u.email, u.first_name, u.preferred_language,
         (ev.id IS NOT NULL AND ch.id IS NOT NULL AND u.is_locked = 0
          AND (IFNULL(JSON_EXTRACT(ev.metadata, '$.mandatory') = TRUE, 0) = 1
               OR (u.email_verified = 1 AND IFNULL((SELECT np.enabled FROM notification_preferences np WHERE np.user_id = u.id AND np.event_code = p_event AND np.channel = p_channel), 1) = 1))) AS enabled
  FROM users u
  LEFT JOIN lookup_values ev ON ev.type_code = 'notification_event'   AND ev.code = p_event   AND ev.is_active = 1
  LEFT JOIN lookup_values ch ON ch.type_code = 'notification_channel' AND ch.code = p_channel AND ch.is_active = 1
  WHERE u.id = p_user_id;
END$$

CREATE PROCEDURE sp_notification_prefs_list(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT event_code, channel, enabled FROM notification_preferences WHERE user_id = p_user_id ORDER BY event_code, channel;
END$$

CREATE PROCEDURE sp_notification_prefs_set(IN p_user_id BIGINT UNSIGNED, IN p_event VARCHAR(60), IN p_channel VARCHAR(20), IN p_enabled TINYINT(1))
BEGIN
  DECLARE v_meta JSON;
  DECLARE v_found INT DEFAULT 0;
  SELECT 1, metadata INTO v_found, v_meta FROM lookup_values WHERE type_code = 'notification_event' AND code = p_event AND is_active = 1;
  IF v_found = 0 OR NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'notification_channel' AND code = p_channel AND is_active = 1)
     OR IFNULL(JSON_EXTRACT(v_meta, '$.mandatory') = TRUE, 0) = 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOTIFICATION_NOT_ALLOWED';
  END IF;
  INSERT INTO notification_preferences (user_id, event_code, channel, enabled) VALUES (p_user_id, p_event, p_channel, p_enabled)
  ON DUPLICATE KEY UPDATE enabled = VALUES(enabled);
END$$

CREATE PROCEDURE sp_email_outbox_create(
  IN p_user_id BIGINT UNSIGNED, IN p_to VARCHAR(255), IN p_event VARCHAR(60), IN p_language VARCHAR(10), IN p_subject VARCHAR(300), IN p_text MEDIUMTEXT, IN p_html MEDIUMTEXT)
BEGIN
  INSERT INTO email_outbox (user_id, to_address, event_code, language, subject, body_text, body_html) VALUES (p_user_id, p_to, p_event, p_language, LEFT(p_subject, 300), p_text, p_html);
  SELECT LAST_INSERT_ID() AS id;
END$$

CREATE PROCEDURE sp_email_outbox_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT id, user_id, to_address, event_code, language, subject, body_text, body_html, status, attempts, last_error, created_at, sent_at FROM email_outbox WHERE id = p_id;
END$$

-- sent / skipped clear the message text; an error counts as an attempt and keeps the text so the retry can send it.
CREATE PROCEDURE sp_email_outbox_mark(IN p_id BIGINT UNSIGNED, IN p_status VARCHAR(10), IN p_error TEXT)
BEGIN
  IF p_status NOT IN ('sent', 'failed', 'skipped', 'queued') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'VALIDATION_FAILED';
  END IF;
  UPDATE email_outbox
     SET status = p_status, attempts = attempts + IF(p_error IS NULL, 0, 1), last_error = LEFT(p_error, 1000),
         sent_at = IF(p_status = 'sent', UTC_TIMESTAMP(3), sent_at),
         body_text = IF(p_status IN ('sent', 'skipped'), NULL, body_text), body_html = IF(p_status IN ('sent', 'skipped'), NULL, body_html)
   WHERE id = p_id;
END$$

-- For the back office: who was e-mailed what and how it went. Never the content.
CREATE PROCEDURE sp_email_outbox_list(IN p_status VARCHAR(10), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 25), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SELECT id, user_id, to_address, event_code, language, subject, status, attempts, last_error, created_at, sent_at
  FROM email_outbox WHERE p_status IS NULL OR status = p_status ORDER BY id DESC LIMIT v_size OFFSET v_offset;
  SELECT COUNT(*) AS total FROM email_outbox WHERE p_status IS NULL OR status = p_status;
END$$

-- How many messages in this conversation has this user not read yet? (decides whether a delayed "new message" e-mail is still needed)
CREATE PROCEDURE sp_chat_room_unread(IN p_room_id BIGINT UNSIGNED, IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT COUNT(*) AS unread FROM chat_messages m JOIN chat_rooms r ON r.id = m.room_id
   WHERE m.room_id = p_room_id AND (r.user_a_id = p_user_id OR r.user_b_id = p_user_id) AND m.sender_id <> p_user_id AND m.is_read = 0 AND m.is_staff = 0 AND m.deleted_at IS NULL
     AND m.id > IFNULL((SELECT cu.cleared_up_to FROM chat_room_users cu WHERE cu.room_id = p_room_id AND cu.user_id = p_user_id), 0);
END$$

DELIMITER ;
