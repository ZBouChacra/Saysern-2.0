-- Repeatable: review replies and moderation, chat deletion and staff moderation. Staff actions are audited in the same transaction.

DROP PROCEDURE IF EXISTS sp_reviews_reply;
DROP PROCEDURE IF EXISTS sp_admin_reviews_list;
DROP PROCEDURE IF EXISTS sp_admin_reviews_set_deleted;
DROP PROCEDURE IF EXISTS sp_chat_message_delete;
DROP PROCEDURE IF EXISTS sp_chat_room_delete;
DROP PROCEDURE IF EXISTS sp_admin_chats_list;
DROP PROCEDURE IF EXISTS sp_admin_chat_messages;
DROP PROCEDURE IF EXISTS sp_admin_chat_post;
DROP PROCEDURE IF EXISTS sp_admin_chat_file;

DELIMITER $$

-- ------------------------------------------------------------------ reviews
-- The reviewed professional answers (or edits their answer). Hidden reviews cannot be answered.
CREATE PROCEDURE sp_reviews_reply(IN p_professional_id BIGINT UNSIGNED, IN p_review_id BIGINT UNSIGNED, IN p_text TEXT)
BEGIN
  DECLARE v_pro BIGINT UNSIGNED;
  DECLARE v_deleted TINYINT;
  DECLARE v_replied DATETIME(3);
  IF p_text IS NULL OR CHAR_LENGTH(TRIM(p_text)) = 0
     OR CHAR_LENGTH(p_text) > (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Reviews.MaxReplyLength') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_REPLY';
  END IF;
  SELECT professional_id, is_deleted, replied_at INTO v_pro, v_deleted, v_replied FROM reviews WHERE id = p_review_id FOR UPDATE;
  IF v_pro IS NULL OR v_pro <> p_professional_id OR v_deleted = 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REVIEW_NOT_FOUND';
  END IF;
  -- (MySQL applies the assignments left to right: the edit time must be decided BEFORE replied_at is filled in)
  UPDATE reviews SET reply_text = TRIM(p_text), reply_edited_at = IF(replied_at IS NULL, NULL, UTC_TIMESTAMP(3)), replied_at = IFNULL(replied_at, UTC_TIMESTAMP(3)) WHERE id = p_review_id;
  SELECT r.id, r.rating, r.comment, r.created_at, u.first_name AS reviewer_first_name, u.last_name AS reviewer_last_name, u.profile_photo AS reviewer_photo,
         r.reply_text, r.replied_at, r.reply_edited_at, r.reviewer_id, (v_replied IS NULL) AS first_reply
  FROM reviews r JOIN users u ON u.id = r.reviewer_id WHERE r.id = p_review_id;
END$$

-- All reviews for staff, hidden ones included. p_status: 'all', 'visible' or 'hidden'.
CREATE PROCEDURE sp_admin_reviews_list(IN p_status VARCHAR(10), IN p_search VARCHAR(100), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(300);
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 25), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = IF(p_search IS NULL OR CHAR_LENGTH(TRIM(p_search)) = 0, NULL, CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_search), '|', '||'), '%', '|%'), '_', '|_'), '%'));
  SELECT r.id, r.rating, r.comment, r.created_at, r.reply_text, r.replied_at, r.is_deleted, r.deleted_at, r.delete_reason,
         r.reviewer_id, CONCAT(rv.first_name, ' ', rv.last_name) AS reviewer_name, r.professional_id, CONCAT(pr.first_name, ' ', pr.last_name) AS professional_name,
         (SELECT CONCAT(du.first_name, ' ', du.last_name) FROM users du WHERE du.id = r.deleted_by) AS deleted_by_name
  FROM reviews r JOIN users rv ON rv.id = r.reviewer_id JOIN users pr ON pr.id = r.professional_id
  WHERE (p_status IS NULL OR p_status = 'all' OR (p_status = 'visible' AND r.is_deleted = 0) OR (p_status = 'hidden' AND r.is_deleted = 1))
    AND (v_like IS NULL OR r.comment LIKE v_like ESCAPE '|' OR r.reply_text LIKE v_like ESCAPE '|' OR CONCAT(rv.first_name, ' ', rv.last_name) LIKE v_like ESCAPE '|' OR CONCAT(pr.first_name, ' ', pr.last_name) LIKE v_like ESCAPE '|')
  ORDER BY r.id DESC LIMIT v_size OFFSET v_offset;
  SELECT COUNT(*) AS total FROM reviews r JOIN users rv ON rv.id = r.reviewer_id JOIN users pr ON pr.id = r.professional_id
  WHERE (p_status IS NULL OR p_status = 'all' OR (p_status = 'visible' AND r.is_deleted = 0) OR (p_status = 'hidden' AND r.is_deleted = 1))
    AND (v_like IS NULL OR r.comment LIKE v_like ESCAPE '|' OR r.reply_text LIKE v_like ESCAPE '|' OR CONCAT(rv.first_name, ' ', rv.last_name) LIKE v_like ESCAPE '|' OR CONCAT(pr.first_name, ' ', pr.last_name) LIKE v_like ESCAPE '|');
END$$

-- Hide or restore a review. A hidden review no longer counts towards the professional's rating (which is recomputed here).
CREATE PROCEDURE sp_admin_reviews_set_deleted(IN p_actor_id BIGINT UNSIGNED, IN p_review_id BIGINT UNSIGNED, IN p_deleted TINYINT(1), IN p_reason VARCHAR(300))
BEGIN
  DECLARE v_pro BIGINT UNSIGNED;
  DECLARE v_now TINYINT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_deleted = 1 AND (p_reason IS NULL OR CHAR_LENGTH(TRIM(p_reason)) = 0) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REASON_REQUIRED';
  END IF;
  START TRANSACTION;
  SELECT professional_id, is_deleted INTO v_pro, v_now FROM reviews WHERE id = p_review_id FOR UPDATE;
  IF v_pro IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REVIEW_NOT_FOUND';
  END IF;
  IF v_now <> p_deleted THEN
    UPDATE reviews SET is_deleted = p_deleted, deleted_at = IF(p_deleted = 1, UTC_TIMESTAMP(3), NULL), deleted_by = IF(p_deleted = 1, p_actor_id, NULL),
           delete_reason = IF(p_deleted = 1, TRIM(p_reason), NULL) WHERE id = p_review_id;
    UPDATE professions
       SET avg_rating = (SELECT IFNULL(AVG(rating), 0) FROM reviews WHERE professional_id = v_pro AND is_deleted = 0),
           total_reviews = (SELECT COUNT(*) FROM reviews WHERE professional_id = v_pro AND is_deleted = 0)
     WHERE user_id = v_pro;
    CALL sp_audit_write(p_actor_id, IF(p_deleted = 1, 'reviews.hide', 'reviews.restore'), 'review', p_review_id,
                        CONCAT(IF(p_deleted = 1, 'Hid', 'Restored'), ' review #', p_review_id, IF(p_deleted = 1, CONCAT(': ', TRIM(p_reason)), '')), JSON_OBJECT('reason', TRIM(p_reason)), NULL);
  END IF;
  COMMIT;
  SELECT r.id, r.rating, r.comment, r.created_at, r.reply_text, r.replied_at, r.is_deleted, r.deleted_at, r.delete_reason, r.reviewer_id,
         CONCAT(rv.first_name, ' ', rv.last_name) AS reviewer_name, r.professional_id, CONCAT(pr.first_name, ' ', pr.last_name) AS professional_name,
         (SELECT CONCAT(du.first_name, ' ', du.last_name) FROM users du WHERE du.id = r.deleted_by) AS deleted_by_name
  FROM reviews r JOIN users rv ON rv.id = r.reviewer_id JOIN users pr ON pr.id = r.professional_id WHERE r.id = p_review_id;
END$$

-- ------------------------------------------------------------------ chat: what the two people can do
-- Deletes one of the caller's own messages for everyone. Staff messages cannot be deleted by users.
CREATE PROCEDURE sp_chat_message_delete(IN p_user_id BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED, IN p_message_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT m.id INTO v_id FROM chat_messages m JOIN chat_rooms r ON r.id = m.room_id
   WHERE m.id = p_message_id AND m.room_id = p_room_id AND m.sender_id = p_user_id AND m.is_staff = 0 AND m.deleted_at IS NULL
     AND (r.user_a_id = p_user_id OR r.user_b_id = p_user_id) FOR UPDATE;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MESSAGE_NOT_FOUND';
  END IF;
  UPDATE chat_messages SET deleted_at = UTC_TIMESTAMP(3), deleted_by = p_user_id WHERE id = v_id;
END$$

-- Deletes the conversation for the caller only: their history is cleared. The other person keeps theirs, and staff can still see all of it.
CREATE PROCEDURE sp_chat_room_delete(IN p_user_id BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_room BIGINT UNSIGNED;
  DECLARE v_last BIGINT UNSIGNED;
  SELECT id INTO v_room FROM chat_rooms WHERE id = p_room_id AND (user_a_id = p_user_id OR user_b_id = p_user_id);
  IF v_room IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROOM_NOT_FOUND';
  END IF;
  SELECT IFNULL(MAX(id), 0) INTO v_last FROM chat_messages WHERE room_id = p_room_id;
  INSERT INTO chat_room_users (room_id, user_id, deleted_at, cleared_up_to) VALUES (p_room_id, p_user_id, UTC_TIMESTAMP(3), v_last)
  ON DUPLICATE KEY UPDATE deleted_at = UTC_TIMESTAMP(3), cleared_up_to = v_last;
END$$

-- ------------------------------------------------------------------ chat: staff
-- Every conversation, newest first. p_filter 'deleted' = conversations where someone deleted the chat or a message.
CREATE PROCEDURE sp_admin_chats_list(IN p_filter VARCHAR(10), IN p_search VARCHAR(100), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(300);
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 25), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = IF(p_search IS NULL OR CHAR_LENGTH(TRIM(p_search)) = 0, NULL, CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_search), '|', '||'), '%', '|%'), '_', '|_'), '%'));
  SELECT * FROM (
    SELECT r.id, r.user_a_id, CONCAT(a.first_name, ' ', a.last_name) AS user_a_name, a.email AS user_a_email,
           r.user_b_id, CONCAT(b.first_name, ' ', b.last_name) AS user_b_name, b.email AS user_b_email, r.created_at, r.last_message_at,
           (SELECT COUNT(*) FROM chat_messages m WHERE m.room_id = r.id) AS message_count,
           (SELECT COUNT(*) FROM chat_messages m WHERE m.room_id = r.id AND m.deleted_at IS NOT NULL) AS deleted_messages,
           (SELECT COUNT(*) FROM chat_messages m WHERE m.room_id = r.id AND m.is_staff = 1) AS staff_messages,
           (SELECT cu.deleted_at FROM chat_room_users cu WHERE cu.room_id = r.id AND cu.user_id = r.user_a_id) AS deleted_by_a_at,
           (SELECT cu.deleted_at FROM chat_room_users cu WHERE cu.room_id = r.id AND cu.user_id = r.user_b_id) AS deleted_by_b_at
    FROM chat_rooms r JOIN users a ON a.id = r.user_a_id JOIN users b ON b.id = r.user_b_id
    WHERE v_like IS NULL OR a.email LIKE v_like ESCAPE '|' OR b.email LIKE v_like ESCAPE '|' OR CONCAT(a.first_name, ' ', a.last_name) LIKE v_like ESCAPE '|' OR CONCAT(b.first_name, ' ', b.last_name) LIKE v_like ESCAPE '|'
  ) x
  WHERE p_filter IS NULL OR p_filter <> 'deleted' OR x.deleted_messages > 0 OR x.deleted_by_a_at IS NOT NULL OR x.deleted_by_b_at IS NOT NULL
  ORDER BY x.last_message_at DESC, x.id DESC LIMIT v_size OFFSET v_offset;
  SELECT COUNT(*) AS total FROM (
    SELECT r.id,
           (SELECT COUNT(*) FROM chat_messages m WHERE m.room_id = r.id AND m.deleted_at IS NOT NULL) AS deleted_messages,
           (SELECT cu.deleted_at FROM chat_room_users cu WHERE cu.room_id = r.id AND cu.user_id = r.user_a_id) AS deleted_by_a_at,
           (SELECT cu.deleted_at FROM chat_room_users cu WHERE cu.room_id = r.id AND cu.user_id = r.user_b_id) AS deleted_by_b_at
    FROM chat_rooms r JOIN users a ON a.id = r.user_a_id JOIN users b ON b.id = r.user_b_id
    WHERE v_like IS NULL OR a.email LIKE v_like ESCAPE '|' OR b.email LIKE v_like ESCAPE '|' OR CONCAT(a.first_name, ' ', a.last_name) LIKE v_like ESCAPE '|' OR CONCAT(b.first_name, ' ', b.last_name) LIKE v_like ESCAPE '|'
  ) x
  WHERE p_filter IS NULL OR p_filter <> 'deleted' OR x.deleted_messages > 0 OR x.deleted_by_a_at IS NOT NULL OR x.deleted_by_b_at IS NOT NULL;
END$$

-- Reads a whole conversation, deleted messages included (with who deleted them). Every reading is written to the audit log.
-- Two result sets: the participants, then the messages (newest first).
CREATE PROCEDURE sp_admin_chat_messages(IN p_actor_id BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED, IN p_limit INT, IN p_offset INT)
BEGIN
  DECLARE v_a VARCHAR(220);
  DECLARE v_b VARCHAR(220);
  DECLARE v_limit INT;
  DECLARE v_offset INT;
  SELECT CONCAT(a.first_name, ' ', a.last_name), CONCAT(b.first_name, ' ', b.last_name) INTO v_a, v_b
    FROM chat_rooms r JOIN users a ON a.id = r.user_a_id JOIN users b ON b.id = r.user_b_id WHERE r.id = p_room_id;
  IF v_a IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROOM_NOT_FOUND';
  END IF;
  SET v_limit = LEAST(GREATEST(IFNULL(p_limit, 100), 1), 200);
  SET v_offset = GREATEST(IFNULL(p_offset, 0), 0);
  CALL sp_audit_write(p_actor_id, 'chats.read', 'chat_room', p_room_id, CONCAT('Read the conversation between ', v_a, ' and ', v_b), JSON_OBJECT('offset', v_offset), NULL);
  SELECT r.id, r.user_a_id, CONCAT(a.first_name, ' ', a.last_name) AS user_a_name, r.user_b_id, CONCAT(b.first_name, ' ', b.last_name) AS user_b_name
  FROM chat_rooms r JOIN users a ON a.id = r.user_a_id JOIN users b ON b.id = r.user_b_id WHERE r.id = p_room_id;
  SELECT m.id, m.room_id, m.sender_id, CONCAT(s.first_name, ' ', s.last_name) AS sender_name, m.message_type, m.content, m.latitude, m.longitude, m.file_id, m.is_read, m.is_staff,
         m.created_at, m.deleted_at, m.deleted_by, (SELECT CONCAT(d.first_name, ' ', d.last_name) FROM users d WHERE d.id = m.deleted_by) AS deleted_by_name
  FROM chat_messages m JOIN users s ON s.id = m.sender_id WHERE m.room_id = p_room_id ORDER BY m.id DESC LIMIT v_limit OFFSET v_offset;
END$$

-- Staff write in a conversation. Visible to both people, highlighted as the team; never counted as unread (they are e-mailed instead).
CREATE PROCEDURE sp_admin_chat_post(IN p_actor_id BIGINT UNSIGNED, IN p_room_id BIGINT UNSIGNED, IN p_content TEXT)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_a BIGINT UNSIGNED;
  DECLARE v_b BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_content IS NULL OR CHAR_LENGTH(TRIM(p_content)) = 0 OR CHAR_LENGTH(p_content) > (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Chat.MaxMessageLength') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_MESSAGE';
  END IF;
  START TRANSACTION;
  SELECT user_a_id, user_b_id INTO v_a, v_b FROM chat_rooms WHERE id = p_room_id FOR UPDATE;
  IF v_a IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROOM_NOT_FOUND';
  END IF;
  INSERT INTO chat_messages (room_id, sender_id, message_type, content, is_read, is_staff) VALUES (p_room_id, p_actor_id, 'text', TRIM(p_content), 1, 1);
  SET v_id = LAST_INSERT_ID();
  UPDATE chat_rooms SET last_message_at = UTC_TIMESTAMP(3) WHERE id = p_room_id;
  UPDATE chat_room_users SET deleted_at = NULL WHERE room_id = p_room_id;       -- a staff message brings the conversation back for both people
  CALL sp_audit_write(p_actor_id, 'chats.post', 'chat_room', p_room_id, CONCAT('Wrote in conversation #', p_room_id, ' as the team'), JSON_OBJECT('messageId', v_id), NULL);
  COMMIT;
  SELECT m.id, m.room_id, m.sender_id, CONCAT(s.first_name, ' ', s.last_name) AS sender_name, m.message_type, m.content, m.latitude, m.longitude, m.file_id, m.is_read, m.is_staff,
         m.created_at, m.deleted_at, m.deleted_by, NULL AS deleted_by_name
  FROM chat_messages m JOIN users s ON s.id = m.sender_id WHERE m.id = v_id;
END$$

-- A file of this conversation, for staff (the picture or video in a message, deleted or not).
CREATE PROCEDURE sp_admin_chat_file(IN p_room_id BIGINT UNSIGNED, IN p_file_id CHAR(32))
BEGIN
  SELECT f.id FROM files f WHERE f.id = p_file_id AND f.purpose = 'chat_media' AND f.room_id = p_room_id
     AND EXISTS (SELECT 1 FROM chat_messages m WHERE m.file_id = f.id AND m.room_id = p_room_id);
END$$

DELIMITER ;
