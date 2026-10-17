-- Repeatable: contact messages.

DROP PROCEDURE IF EXISTS sp_contact_create;
DROP PROCEDURE IF EXISTS sp_contact_list;
DROP PROCEDURE IF EXISTS sp_contact_get;
DROP PROCEDURE IF EXISTS sp_contact_reply;
DROP PROCEDURE IF EXISTS sp_contact_set_status;

DELIMITER $$

-- Stores a message from the public form. p_spam = the hidden trap field was filled in: the message is kept (for review) but marked spam.
CREATE PROCEDURE sp_contact_create(IN p_user_id BIGINT UNSIGNED, IN p_name VARCHAR(100), IN p_email VARCHAR(255), IN p_language VARCHAR(10), IN p_topic VARCHAR(40),
                                   IN p_subject VARCHAR(150), IN p_body TEXT, IN p_spam TINYINT(1))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  IF p_name IS NULL OR CHAR_LENGTH(TRIM(p_name)) = 0 OR p_email IS NULL OR p_email NOT REGEXP '^[^@ ]+@[^@ ]+\\.[^@ ]+$' OR p_subject IS NULL OR CHAR_LENGTH(TRIM(p_subject)) = 0
     OR p_body IS NULL OR CHAR_LENGTH(TRIM(p_body)) = 0 OR CHAR_LENGTH(p_body) > (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Contact.MaxMessageLength') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CONTACT_INVALID';
  END IF;
  IF p_topic IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'contact_topic' AND code = p_topic AND is_active = 1) THEN
    SET p_topic = NULL;
  END IF;
  INSERT INTO contact_messages (user_id, name, email, language, topic, subject, body, status)
  VALUES (p_user_id, TRIM(p_name), LOWER(TRIM(p_email)), p_language, p_topic, TRIM(p_subject), TRIM(p_body), IF(p_spam = 1, 'spam', 'new'));
  SET v_id = LAST_INSERT_ID();
  SELECT id, status FROM contact_messages WHERE id = v_id;
END$$

CREATE PROCEDURE sp_contact_list(IN p_status VARCHAR(12), IN p_search VARCHAR(100), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(300);
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 25), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = IF(p_search IS NULL OR CHAR_LENGTH(TRIM(p_search)) = 0, NULL, CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_search), '|', '||'), '%', '|%'), '_', '|_'), '%'));
  SELECT id, user_id, name, email, language, topic, subject, LEFT(body, 160) AS preview, status, created_at, replied_at
  FROM contact_messages
  WHERE (p_status IS NULL OR p_status = 'all' OR status = p_status)
    AND (v_like IS NULL OR name LIKE v_like ESCAPE '|' OR email LIKE v_like ESCAPE '|' OR subject LIKE v_like ESCAPE '|' OR body LIKE v_like ESCAPE '|')
  ORDER BY (status = 'new') DESC, id DESC LIMIT v_size OFFSET v_offset;
  SELECT COUNT(*) AS total FROM contact_messages
  WHERE (p_status IS NULL OR p_status = 'all' OR status = p_status)
    AND (v_like IS NULL OR name LIKE v_like ESCAPE '|' OR email LIKE v_like ESCAPE '|' OR subject LIKE v_like ESCAPE '|' OR body LIKE v_like ESCAPE '|');
END$$

CREATE PROCEDURE sp_contact_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT m.id, m.user_id, m.name, m.email, m.language, m.topic, m.subject, m.body, m.status, m.reply_body, m.replied_at, m.internal_note, m.created_at,
         (SELECT CONCAT(u.first_name, ' ', u.last_name) FROM users u WHERE u.id = m.replied_by) AS replied_by_name,
         (SELECT t.name FROM lookup_values v JOIN lookup_translations t ON t.value_id = v.id AND t.language = 'en' WHERE v.type_code = 'contact_topic' AND v.code = m.topic) AS topic_name
  FROM contact_messages m WHERE m.id = p_id;
END$$

-- Records the reply that was e-mailed (the e-mail itself is queued by the API in the same request).
CREATE PROCEDURE sp_contact_reply(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_reply TEXT)
BEGIN
  DECLARE v_email VARCHAR(255);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_reply IS NULL OR CHAR_LENGTH(TRIM(p_reply)) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REPLY_REQUIRED';
  END IF;
  START TRANSACTION;
  SELECT email INTO v_email FROM contact_messages WHERE id = p_id FOR UPDATE;
  IF v_email IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CONTACT_NOT_FOUND';
  END IF;
  UPDATE contact_messages SET reply_body = TRIM(p_reply), replied_at = UTC_TIMESTAMP(3), replied_by = p_actor_id, status = 'replied', updated_at = UTC_TIMESTAMP(3) WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'contact.reply', 'contact_message', p_id, CONCAT('Replied to contact message #', p_id, ' (', v_email, ')'), NULL, NULL);
  COMMIT;
  CALL sp_contact_get(p_id);
END$$

CREATE PROCEDURE sp_contact_set_status(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_status VARCHAR(12), IN p_note VARCHAR(1000))
BEGIN
  DECLARE v_old VARCHAR(12);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_status NOT IN ('new', 'in_progress', 'replied', 'archived', 'spam') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_STATUS';
  END IF;
  START TRANSACTION;
  SELECT status INTO v_old FROM contact_messages WHERE id = p_id FOR UPDATE;
  IF v_old IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CONTACT_NOT_FOUND';
  END IF;
  UPDATE contact_messages SET status = p_status, internal_note = IF(p_note IS NULL, internal_note, NULLIF(TRIM(p_note), '')), updated_at = UTC_TIMESTAMP(3) WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'contact.status', 'contact_message', p_id, CONCAT('Moved contact message #', p_id, ' from ', v_old, ' to ', p_status), JSON_OBJECT('from', v_old, 'to', p_status), NULL);
  COMMIT;
  CALL sp_contact_get(p_id);
END$$

DELIMITER ;
