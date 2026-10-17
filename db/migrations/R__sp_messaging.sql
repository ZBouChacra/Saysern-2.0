-- Repeatable: chat and saved-search alerts.
-- Chat access is membership-checked here: a user can only read or write rooms they belong to.

DROP PROCEDURE IF EXISTS sp_chat_start;
DROP PROCEDURE IF EXISTS sp_chat_rooms_list;
DROP PROCEDURE IF EXISTS sp_chat_messages_list;
DROP PROCEDURE IF EXISTS sp_chat_send;
DROP PROCEDURE IF EXISTS sp_chat_unread_count;
DROP PROCEDURE IF EXISTS sp_alerts_create;
DROP PROCEDURE IF EXISTS sp_alerts_list;
DROP PROCEDURE IF EXISTS sp_alerts_get;
DROP PROCEDURE IF EXISTS sp_alerts_delete;

DELIMITER $$

-- Finds or creates the room between two users (same room whoever starts it).
CREATE PROCEDURE sp_chat_start(IN p_user_id BIGINT UNSIGNED, IN p_other_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_other BIGINT UNSIGNED;
  DECLARE v_id BIGINT UNSIGNED;
  IF (SELECT value_text FROM app_settings WHERE setting_key = 'Auth.RequireVerifiedEmail') = 'true'
     AND (SELECT value_text FROM app_settings WHERE setting_key = 'Email.Provider') <> 'off'
     AND NOT EXISTS (SELECT 1 FROM users WHERE id = p_user_id AND email_verified = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'EMAIL_NOT_VERIFIED';
  END IF;
  IF p_user_id = p_other_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CANNOT_CHAT_SELF';
  END IF;
  SELECT id INTO v_other FROM users WHERE id = p_other_id AND is_locked = 0 AND is_disabled = 0;
  IF v_other IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  INSERT INTO chat_rooms (user_a_id, user_b_id)
  VALUES (LEAST(p_user_id, p_other_id), GREATEST(p_user_id, p_other_id))
  ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id);
  SET v_id = LAST_INSERT_ID();
  UPDATE chat_room_users SET deleted_at = NULL WHERE room_id = v_id AND user_id = p_user_id;     -- opening a conversation again brings it back (what was cleared stays cleared)
  SELECT id, last_message_at, created_at FROM chat_rooms WHERE id = v_id;
END$$

CREATE PROCEDURE sp_chat_rooms_list(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT r.id, o.id AS other_user_id, o.first_name AS other_first_name, o.last_name AS other_last_name,
         o.profile_photo AS other_photo, r.last_message_at,
         (SELECT IF(m.deleted_at IS NULL, m.message_type, 'deleted') FROM chat_messages m WHERE m.room_id = r.id AND m.id > IFNULL(cu.cleared_up_to, 0) ORDER BY m.id DESC LIMIT 1) AS last_message_type,
         (SELECT IF(m.deleted_at IS NULL, m.content, NULL) FROM chat_messages m WHERE m.room_id = r.id AND m.id > IFNULL(cu.cleared_up_to, 0) ORDER BY m.id DESC LIMIT 1) AS last_message_content,
         (SELECT m.sender_id FROM chat_messages m WHERE m.room_id = r.id AND m.id > IFNULL(cu.cleared_up_to, 0) ORDER BY m.id DESC LIMIT 1) AS last_message_sender_id,
         (SELECT COUNT(*) FROM chat_messages m WHERE m.room_id = r.id AND m.sender_id <> p_user_id AND m.is_read = 0 AND m.is_staff = 0 AND m.deleted_at IS NULL AND m.id > IFNULL(cu.cleared_up_to, 0)) AS unread_count
  FROM chat_rooms r
  JOIN users o ON o.id = IF(r.user_a_id = p_user_id, r.user_b_id, r.user_a_id)
  LEFT JOIN chat_room_users cu ON cu.room_id = r.id AND cu.user_id = p_user_id
  WHERE (r.user_a_id = p_user_id OR r.user_b_id = p_user_id)
    AND (cu.deleted_at IS NULL OR EXISTS (SELECT 1 FROM chat_messages m2 WHERE m2.room_id = r.id AND m2.id > cu.cleared_up_to))   -- a deleted conversation stays hidden until something newer arrives
  ORDER BY r.last_message_at DESC, r.id DESC;
END$$

-- Newest first. Reading marks the other person's messages as read.
CREATE PROCEDURE sp_chat_messages_list(IN p_user_id BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED, IN p_limit INT, IN p_offset INT)
BEGIN
  DECLARE v_room BIGINT UNSIGNED;
  DECLARE v_limit INT;
  DECLARE v_offset INT;
  DECLARE v_cleared BIGINT UNSIGNED DEFAULT 0;
  SELECT id INTO v_room FROM chat_rooms WHERE id = p_room_id AND (user_a_id = p_user_id OR user_b_id = p_user_id);
  IF v_room IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROOM_NOT_FOUND';
  END IF;
  SET v_limit = LEAST(GREATEST(IFNULL(p_limit, 50), 1), 100);
  SET v_offset = GREATEST(IFNULL(p_offset, 0), 0);

  SELECT IFNULL(MAX(cleared_up_to), 0) INTO v_cleared FROM chat_room_users WHERE room_id = p_room_id AND user_id = p_user_id;
  UPDATE chat_messages SET is_read = 1 WHERE room_id = p_room_id AND sender_id <> p_user_id AND is_read = 0 AND is_staff = 0;

  -- a deleted message shows only that it was deleted; what the person cleared is not returned
  SELECT id, room_id, sender_id, message_type, IF(deleted_at IS NULL, content, NULL) AS content, IF(deleted_at IS NULL, latitude, NULL) AS latitude,
         IF(deleted_at IS NULL, longitude, NULL) AS longitude, IF(deleted_at IS NULL, file_id, NULL) AS file_id, is_read, created_at, is_staff, (deleted_at IS NOT NULL) AS is_deleted
  FROM chat_messages WHERE room_id = p_room_id AND id > v_cleared
  ORDER BY id DESC
  LIMIT v_limit OFFSET v_offset;
END$$

CREATE PROCEDURE sp_chat_send(
  IN p_user_id BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED, IN p_type VARCHAR(20), IN p_content TEXT,
  IN p_latitude DECIMAL(10,7), IN p_longitude DECIMAL(10,7), IN p_file_id CHAR(32))
BEGIN
  DECLARE v_room BIGINT UNSIGNED;
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_status VARCHAR(12);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF (SELECT value_text FROM app_settings WHERE setting_key = 'Auth.RequireVerifiedEmail') = 'true'
     AND (SELECT value_text FROM app_settings WHERE setting_key = 'Email.Provider') <> 'off'
     AND NOT EXISTS (SELECT 1 FROM users WHERE id = p_user_id AND email_verified = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'EMAIL_NOT_VERIFIED';
  END IF;

  SELECT id INTO v_room FROM chat_rooms WHERE id = p_room_id AND (user_a_id = p_user_id OR user_b_id = p_user_id);
  IF v_room IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROOM_NOT_FOUND';
  END IF;
  IF p_type = 'text' THEN
    IF p_content IS NULL OR CHAR_LENGTH(TRIM(p_content)) = 0 OR CHAR_LENGTH(p_content) > (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Chat.MaxMessageLength') THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_MESSAGE';
    END IF;
  ELSEIF p_type = 'location' THEN
    IF p_latitude IS NULL OR p_longitude IS NULL OR p_latitude NOT BETWEEN -90 AND 90 OR p_longitude NOT BETWEEN -180 AND 180 THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_MESSAGE';
    END IF;
  ELSEIF p_type IN ('image', 'video') THEN
    -- the file must be the sender's own chat media for THIS room, of the matching kind, and fully processed
    SELECT status INTO v_status FROM files
     WHERE id = p_file_id AND owner_id = p_user_id AND purpose = 'chat_media' AND room_id = p_room_id AND media_kind = p_type AND is_deleted = 0;
    IF v_status IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FILE_NOT_FOUND';
    END IF;
    IF v_status <> 'ready' THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FILE_NOT_READY';
    END IF;
    IF p_content IS NOT NULL AND CHAR_LENGTH(p_content) > (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Chat.MaxMessageLength') THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_MESSAGE';
    END IF;
  ELSE
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_MESSAGE';
  END IF;

  START TRANSACTION;
  INSERT INTO chat_messages (room_id, sender_id, message_type, content, latitude, longitude, file_id)
  VALUES (p_room_id, p_user_id, p_type, IF(p_type IN ('text', 'image', 'video'), NULLIF(TRIM(p_content), ''), NULL),
          IF(p_type = 'location', p_latitude, NULL), IF(p_type = 'location', p_longitude, NULL), IF(p_type IN ('image', 'video'), p_file_id, NULL));
  SET v_id = LAST_INSERT_ID();
  UPDATE chat_rooms SET last_message_at = UTC_TIMESTAMP(3) WHERE id = p_room_id;
  COMMIT;

  SELECT id, room_id, sender_id, message_type, content, latitude, longitude, file_id, is_read, created_at, is_staff, (deleted_at IS NOT NULL) AS is_deleted
  FROM chat_messages WHERE id = v_id;
END$$

CREATE PROCEDURE sp_chat_unread_count(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT COUNT(*) AS unread
  FROM chat_messages m
  JOIN chat_rooms r ON r.id = m.room_id
  WHERE (r.user_a_id = p_user_id OR r.user_b_id = p_user_id) AND m.sender_id <> p_user_id AND m.is_read = 0 AND m.is_staff = 0 AND m.deleted_at IS NULL
    AND m.id > IFNULL((SELECT cu.cleared_up_to FROM chat_room_users cu WHERE cu.room_id = r.id AND cu.user_id = p_user_id), 0);
END$$

-- Saved searches (p_criteria is the validated search filter object as JSON). At most 20 per user.
CREATE PROCEDURE sp_alerts_create(IN p_user_id BIGINT UNSIGNED, IN p_name VARCHAR(100), IN p_criteria JSON, IN p_frequency VARCHAR(20))
BEGIN
  DECLARE v_count INT;
  DECLARE v_id BIGINT UNSIGNED;
  IF NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'alert_frequency' AND code = p_frequency AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_FREQUENCY';
  END IF;
  SELECT COUNT(*) INTO v_count FROM alerts WHERE user_id = p_user_id;
  IF v_count >= (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Alerts.MaxPerUser') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ALERT_LIMIT';
  END IF;
  INSERT INTO alerts (user_id, name, search_criteria, frequency) VALUES (p_user_id, p_name, p_criteria, p_frequency);
  SET v_id = LAST_INSERT_ID();
  SELECT id, name, search_criteria, frequency, created_at FROM alerts WHERE id = v_id;
END$$

CREATE PROCEDURE sp_alerts_list(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT id, name, search_criteria, frequency, created_at FROM alerts WHERE user_id = p_user_id ORDER BY created_at DESC, id DESC;
END$$

CREATE PROCEDURE sp_alerts_get(IN p_user_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT id, name, search_criteria, frequency, created_at FROM alerts WHERE id = p_id AND user_id = p_user_id;
END$$

-- Owner only: someone else's alert simply "does not exist".
CREATE PROCEDURE sp_alerts_delete(IN p_user_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM alerts WHERE id = p_id AND user_id = p_user_id;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ALERT_NOT_FOUND';
  END IF;
  DELETE FROM alerts WHERE id = p_id;
END$$

DELIMITER ;
