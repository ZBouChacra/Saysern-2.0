-- Repeatable: configuration platform, audit log, roles & permissions, two-factor.
-- Every mutating procedure takes the acting admin's id and writes the audit entry in the SAME transaction as the change.

DROP PROCEDURE IF EXISTS sp_audit_write;
DROP PROCEDURE IF EXISTS sp_audit_list;
DROP PROCEDURE IF EXISTS sp_settings_list;
DROP PROCEDURE IF EXISTS sp_settings_set;
DROP PROCEDURE IF EXISTS sp_settings_reset;
DROP PROCEDURE IF EXISTS sp_lookups_types_list;
DROP PROCEDURE IF EXISTS sp_lookups_types_save;
DROP PROCEDURE IF EXISTS sp_lookups_values_list;
DROP PROCEDURE IF EXISTS sp_lookups_values_save;
DROP PROCEDURE IF EXISTS sp_translations_get;
DROP PROCEDURE IF EXISTS sp_translations_all;
DROP PROCEDURE IF EXISTS sp_translations_save;
DROP PROCEDURE IF EXISTS sp_translations_reset;
DROP PROCEDURE IF EXISTS sp_permissions_list;
DROP PROCEDURE IF EXISTS sp_roles_list;
DROP PROCEDURE IF EXISTS sp_roles_save;
DROP PROCEDURE IF EXISTS sp_roles_delete;
DROP PROCEDURE IF EXISTS sp_user_roles_set;
DROP PROCEDURE IF EXISTS sp_staff_get;
DROP PROCEDURE IF EXISTS sp_twofactor_get;
DROP PROCEDURE IF EXISTS sp_twofactor_begin;
DROP PROCEDURE IF EXISTS sp_twofactor_confirm;
DROP PROCEDURE IF EXISTS sp_twofactor_use_step;
DROP PROCEDURE IF EXISTS sp_twofactor_use_recovery;
DROP PROCEDURE IF EXISTS sp_twofactor_replace_recovery;
DROP PROCEDURE IF EXISTS sp_twofactor_recovery_count;
DROP PROCEDURE IF EXISTS sp_twofactor_reset;
DROP PROCEDURE IF EXISTS sp_twofactor_reset_by_email;
DROP PROCEDURE IF EXISTS sp_catalog_categories_save;
DROP PROCEDURE IF EXISTS sp_catalog_services_save;

DELIMITER $$

-- ------------------------------------------------------------------ audit
CREATE PROCEDURE sp_audit_write(
  IN p_actor_id BIGINT UNSIGNED, IN p_action VARCHAR(100), IN p_entity_type VARCHAR(50), IN p_entity_id VARCHAR(100),
  IN p_summary VARCHAR(500), IN p_details JSON, IN p_ip VARCHAR(45))
BEGIN
  DECLARE v_name VARCHAR(255);
  IF p_actor_id IS NOT NULL THEN
    SELECT CONCAT(first_name, ' ', last_name, ' <', email, '>') INTO v_name FROM users WHERE id = p_actor_id;
  END IF;
  INSERT INTO audit_log (actor_id, actor_name, action, entity_type, entity_id, summary, details, ip)
  VALUES (p_actor_id, v_name, p_action, p_entity_type, p_entity_id, LEFT(p_summary, 500), p_details, p_ip);
END$$

CREATE PROCEDURE sp_audit_list(
  IN p_from DATETIME(3), IN p_to DATETIME(3), IN p_actor_id BIGINT UNSIGNED, IN p_action VARCHAR(100),
  IN p_entity_type VARCHAR(50), IN p_search VARCHAR(200), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(260);
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 50), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = IF(p_search IS NULL OR CHAR_LENGTH(TRIM(p_search)) = 0, NULL,
                  CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_search), '|', '||'), '%', '|%'), '_', '|_'), '%'));

  SELECT id, occurred_at, actor_id, actor_name, action, entity_type, entity_id, summary, details, ip
  FROM audit_log
  WHERE (p_from IS NULL OR occurred_at >= p_from) AND (p_to IS NULL OR occurred_at < p_to)
    AND (p_actor_id IS NULL OR actor_id = p_actor_id) AND (p_action IS NULL OR action = p_action)
    AND (p_entity_type IS NULL OR entity_type = p_entity_type)
    AND (v_like IS NULL OR summary LIKE v_like ESCAPE '|' OR actor_name LIKE v_like ESCAPE '|' OR entity_id LIKE v_like ESCAPE '|')
  ORDER BY occurred_at DESC, id DESC
  LIMIT v_size OFFSET v_offset;

  SELECT COUNT(*) AS total
  FROM audit_log
  WHERE (p_from IS NULL OR occurred_at >= p_from) AND (p_to IS NULL OR occurred_at < p_to)
    AND (p_actor_id IS NULL OR actor_id = p_actor_id) AND (p_action IS NULL OR action = p_action)
    AND (p_entity_type IS NULL OR entity_type = p_entity_type)
    AND (v_like IS NULL OR summary LIKE v_like ESCAPE '|' OR actor_name LIKE v_like ESCAPE '|' OR entity_id LIKE v_like ESCAPE '|');
END$$

-- ------------------------------------------------------------------ settings
CREATE PROCEDURE sp_settings_list()
BEGIN
  SELECT setting_key, group_name, value_type, value_text, default_text, min_value, max_value, description, is_public, updated_at, updated_by
  FROM app_settings ORDER BY group_name, setting_key;
END$$

CREATE PROCEDURE sp_settings_set(IN p_actor_id BIGINT UNSIGNED, IN p_key VARCHAR(100), IN p_value VARCHAR(2000))
BEGIN
  DECLARE v_type VARCHAR(10);
  DECLARE v_min DECIMAL(18,4);
  DECLARE v_max DECIMAL(18,4);
  DECLARE v_old VARCHAR(2000);
  DECLARE v_default VARCHAR(2000);
  DECLARE v_num DECIMAL(30,6);
  DECLARE v_other DECIMAL(30,6);
  DECLARE v_value VARCHAR(2000);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SELECT value_type, min_value, max_value, value_text, default_text INTO v_type, v_min, v_max, v_old, v_default FROM app_settings WHERE setting_key = p_key;
  IF v_type IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_NOT_FOUND';
  END IF;
  SET v_value = TRIM(p_value);
  -- a value may be empty only for secrets (clearing one) and for settings whose default is empty
  IF v_value IS NULL OR (CHAR_LENGTH(v_value) = 0 AND v_type <> 'secret' AND v_default <> '') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
  END IF;

  IF v_type = 'secret' THEN
    IF CHAR_LENGTH(v_value) > 1900 THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
    END IF;
  ELSEIF v_type = 'int' THEN
    IF v_value NOT REGEXP '^-?[0-9]{1,15}$' THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
    END IF;
    SET v_num = CAST(v_value AS DECIMAL(30,6));
  ELSEIF v_type = 'decimal' THEN
    IF v_value NOT REGEXP '^-?[0-9]{1,15}(\\.[0-9]{1,6})?$' THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
    END IF;
    SET v_num = CAST(v_value AS DECIMAL(30,6));
  ELSEIF v_type = 'bool' THEN
    SET v_value = LOWER(v_value);
    IF v_value NOT IN ('true', 'false') THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
    END IF;
  END IF;

  IF v_type IN ('int', 'decimal') AND ((v_min IS NOT NULL AND v_num < v_min) OR (v_max IS NOT NULL AND v_num > v_max)) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_OUT_OF_RANGE';
  END IF;

  IF p_key = 'Site.DefaultLanguage'
     AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'language' AND code = v_value AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_LANGUAGE';
  END IF;
  IF p_key = 'Storage.Provider'
     AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'storage_provider' AND code = v_value AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_STORAGE_PROVIDER';
  END IF;
  IF p_key = 'Email.Provider' AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'email_provider' AND code = v_value AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
  END IF;
  IF p_key = 'Email.SmtpSecurity' AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'smtp_security' AND code = v_value AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
  END IF;
  IF p_key = 'Email.FromAddress' AND v_value NOT REGEXP '^[^@ ]+@[^@ ]+$' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
  END IF;
  IF p_key = 'Site.PublicUrl' AND v_value NOT REGEXP '^https?://[^ /]+(/[^ ]*[^ /])?$' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INVALID';
  END IF;
  -- settings that must stay consistent with each other
  IF p_key = 'Booking.MinDurationMinutes' THEN
    SELECT CAST(value_text AS DECIMAL(30,6)) INTO v_other FROM app_settings WHERE setting_key = 'Booking.MaxDurationMinutes';
    IF v_num > v_other THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INCONSISTENT'; END IF;
  ELSEIF p_key = 'Booking.MaxDurationMinutes' THEN
    SELECT CAST(value_text AS DECIMAL(30,6)) INTO v_other FROM app_settings WHERE setting_key = 'Booking.MinDurationMinutes';
    IF v_num < v_other THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INCONSISTENT'; END IF;
  ELSEIF p_key = 'Search.DefaultPageSize' THEN
    SELECT CAST(value_text AS DECIMAL(30,6)) INTO v_other FROM app_settings WHERE setting_key = 'Search.MaxPageSize';
    IF v_num > v_other THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INCONSISTENT'; END IF;
  ELSEIF p_key = 'Search.MaxPageSize' THEN
    SELECT CAST(value_text AS DECIMAL(30,6)) INTO v_other FROM app_settings WHERE setting_key = 'Search.DefaultPageSize';
    IF v_num < v_other THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_INCONSISTENT'; END IF;
  END IF;

  START TRANSACTION;
  UPDATE app_settings SET value_text = v_value, updated_at = UTC_TIMESTAMP(3), updated_by = p_actor_id WHERE setting_key = p_key;
  IF v_type = 'secret' THEN   -- a secret's value (even encrypted) never goes into the audit log
    CALL sp_audit_write(p_actor_id, 'settings.update', 'setting', p_key, CONCAT(IF(CHAR_LENGTH(v_value) = 0, 'Cleared ', 'Changed '), p_key, ' (secret)'), JSON_OBJECT('secret', TRUE), NULL);
  ELSE
    CALL sp_audit_write(p_actor_id, 'settings.update', 'setting', p_key, CONCAT('Changed ', p_key, ' from ', v_old, ' to ', v_value),
                        JSON_OBJECT('old', v_old, 'new', v_value), NULL);
  END IF;
  COMMIT;
  SELECT setting_key, group_name, value_type, value_text, default_text, min_value, max_value, description, is_public, updated_at, updated_by
  FROM app_settings WHERE setting_key = p_key;
END$$

CREATE PROCEDURE sp_settings_reset(IN p_actor_id BIGINT UNSIGNED, IN p_key VARCHAR(100))
BEGIN
  DECLARE v_default VARCHAR(2000);
  SELECT default_text INTO v_default FROM app_settings WHERE setting_key = p_key;
  IF v_default IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SETTING_NOT_FOUND';
  END IF;
  CALL sp_settings_set(p_actor_id, p_key, v_default);
END$$

-- ------------------------------------------------------------------ lookups
CREATE PROCEDURE sp_lookups_types_list()
BEGIN
  SELECT t.code, t.name, t.description, t.parent_type, t.is_system,
         (SELECT COUNT(*) FROM lookup_values v WHERE v.type_code = t.code) AS value_count
  FROM lookup_types t ORDER BY t.name;
END$$

CREATE PROCEDURE sp_lookups_types_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_code VARCHAR(50), IN p_name VARCHAR(100), IN p_description VARCHAR(300), IN p_parent_type VARCHAR(50))
BEGIN
  DECLARE v_exists VARCHAR(50);
  DECLARE v_system TINYINT(1);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_code IS NULL OR p_code NOT REGEXP '^[a-z][a-z0-9_]{1,49}$' OR p_name IS NULL OR CHAR_LENGTH(TRIM(p_name)) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_CODE';
  END IF;
  IF p_parent_type IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_types WHERE code = p_parent_type) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'LOOKUP_TYPE_NOT_FOUND';
  END IF;
  SELECT code, is_system INTO v_exists, v_system FROM lookup_types WHERE code = p_code;
  START TRANSACTION;
  IF v_exists IS NULL THEN
    INSERT INTO lookup_types (code, name, description, parent_type, is_system) VALUES (p_code, TRIM(p_name), p_description, p_parent_type, 0);
    CALL sp_audit_write(p_actor_id, 'lookups.type.create', 'lookup_type', p_code, CONCAT('Created lookup type ', p_code), NULL, NULL);
  ELSE
    UPDATE lookup_types SET name = TRIM(p_name), description = p_description WHERE code = p_code;   -- the parent of an existing type never changes
    CALL sp_audit_write(p_actor_id, 'lookups.type.update', 'lookup_type', p_code, CONCAT('Edited lookup type ', p_code), NULL, NULL);
  END IF;
  COMMIT;
  SELECT t.code, t.name, t.description, t.parent_type, t.is_system, (SELECT COUNT(*) FROM lookup_values v WHERE v.type_code = t.code) AS value_count
  FROM lookup_types t WHERE t.code = p_code;
END$$

-- Two result sets: the values, then their translations. NULL filters are off. Inactive values only when asked for.
CREATE PROCEDURE sp_lookups_values_list(
  IN p_type VARCHAR(50), IN p_parent_code VARCHAR(100), IN p_code VARCHAR(100), IN p_include_inactive TINYINT(1))
BEGIN
  SELECT v.id, v.type_code, v.code, p.code AS parent_code, v.sort_order, v.is_active, v.metadata
  FROM lookup_values v LEFT JOIN lookup_values p ON p.id = v.parent_id
  WHERE v.type_code = p_type AND (p_parent_code IS NULL OR p.code = p_parent_code) AND (p_code IS NULL OR v.code = p_code)
    AND (IFNULL(p_include_inactive, 0) = 1 OR v.is_active = 1)
  ORDER BY v.sort_order, v.code;

  SELECT t.value_id, t.language, t.name
  FROM lookup_translations t
  JOIN lookup_values v ON v.id = t.value_id
  LEFT JOIN lookup_values p ON p.id = v.parent_id
  WHERE v.type_code = p_type AND (p_parent_code IS NULL OR p.code = p_parent_code) AND (p_code IS NULL OR v.code = p_code)
    AND (IFNULL(p_include_inactive, 0) = 1 OR v.is_active = 1)
  ORDER BY t.value_id, t.language;
END$$

-- Creates or updates one value. p_names is [{"language":"en","name":"..."}, ...] and must include English.
CREATE PROCEDURE sp_lookups_values_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_type VARCHAR(50), IN p_code VARCHAR(100), IN p_parent_code VARCHAR(100),
  IN p_sort_order INT, IN p_is_active TINYINT(1), IN p_metadata JSON, IN p_names JSON)
BEGIN
  DECLARE v_type VARCHAR(50);
  DECLARE v_parent_type VARCHAR(50);
  DECLARE v_parent BIGINT UNSIGNED;
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_bad INT;
  DECLARE v_en INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SELECT code, parent_type INTO v_type, v_parent_type FROM lookup_types WHERE code = p_type;
  IF v_type IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'LOOKUP_TYPE_NOT_FOUND';
  END IF;
  IF p_code IS NULL OR p_code NOT REGEXP '^[A-Za-z0-9_.-]{1,100}$' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_CODE';
  END IF;
  IF v_parent_type IS NOT NULL THEN
    SELECT id INTO v_parent FROM lookup_values WHERE type_code = v_parent_type AND code = p_parent_code;
    IF v_parent IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'LOOKUP_PARENT_REQUIRED';
    END IF;
  END IF;
  IF p_names IS NULL OR JSON_TYPE(p_names) <> 'ARRAY' OR JSON_LENGTH(p_names) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NAMES';
  END IF;
  SELECT COUNT(*) INTO v_bad
  FROM JSON_TABLE(p_names, '$[*]' COLUMNS (language VARCHAR(10) PATH '$.language', name VARCHAR(200) PATH '$.name')) j
  WHERE j.language IS NULL OR j.name IS NULL OR CHAR_LENGTH(TRIM(j.name)) = 0
     OR NOT EXISTS (SELECT 1 FROM lookup_values l WHERE l.type_code = 'language' AND l.code = j.language);
  SELECT COUNT(*) INTO v_en FROM JSON_TABLE(p_names, '$[*]' COLUMNS (language VARCHAR(10) PATH '$.language')) j WHERE j.language = 'en';
  IF v_bad > 0 OR v_en = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NAMES';
  END IF;
  IF p_type = 'language' AND IFNULL(p_is_active, 1) = 0
     AND p_code = (SELECT value_text FROM app_settings WHERE setting_key = 'Site.DefaultLanguage') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DEFAULT_LANGUAGE_REQUIRED';
  END IF;

  SELECT id INTO v_id FROM lookup_values WHERE type_code = p_type AND code = p_code;
  START TRANSACTION;
  IF v_id IS NULL THEN
    INSERT INTO lookup_values (type_code, code, parent_id, sort_order, is_active, metadata)
    VALUES (p_type, p_code, v_parent, IFNULL(p_sort_order, 0), IFNULL(p_is_active, 1), p_metadata);
    SET v_id = LAST_INSERT_ID();
  ELSE
    UPDATE lookup_values SET parent_id = v_parent, sort_order = IFNULL(p_sort_order, sort_order),
           is_active = IFNULL(p_is_active, is_active), metadata = p_metadata WHERE id = v_id;
  END IF;
  INSERT INTO lookup_translations (value_id, language, name)
  SELECT v_id, j.language, TRIM(j.name)
  FROM JSON_TABLE(p_names, '$[*]' COLUMNS (language VARCHAR(10) PATH '$.language', name VARCHAR(200) PATH '$.name')) j
  ON DUPLICATE KEY UPDATE name = VALUES(name);
  CALL sp_audit_write(p_actor_id, 'lookups.value.save', CONCAT('lookup:', p_type), p_code,
                      CONCAT('Saved ', p_type, ' "', p_code, '"', IF(IFNULL(p_is_active, 1) = 0, ' (inactive)', '')),
                      JSON_OBJECT('names', p_names, 'sortOrder', p_sort_order, 'isActive', IFNULL(p_is_active, 1)), NULL);
  COMMIT;
  CALL sp_lookups_values_list(p_type, NULL, p_code, 1);
END$$

-- ------------------------------------------------------------------ translations
-- All keys for one language; a key with no text in that language falls back to the default language.
CREATE PROCEDURE sp_translations_get(IN p_language VARCHAR(10))
BEGIN
  DECLARE v_default VARCHAR(10);
  SELECT value_text INTO v_default FROM app_settings WHERE setting_key = 'Site.DefaultLanguage';
  SELECT d.translation_key, COALESCE(l.value, d.value) AS value
  FROM translations d
  LEFT JOIN translations l ON l.translation_key = d.translation_key AND l.language = p_language
  WHERE d.language = v_default
  ORDER BY d.translation_key;
END$$

CREATE PROCEDURE sp_translations_all()
BEGIN
  SELECT language, translation_key, value, default_value, updated_at FROM translations ORDER BY translation_key, language;
END$$

CREATE PROCEDURE sp_translations_save(IN p_actor_id BIGINT UNSIGNED, IN p_language VARCHAR(10), IN p_key VARCHAR(150), IN p_value TEXT)
BEGIN
  DECLARE v_old TEXT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'language' AND code = p_language) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_LANGUAGE';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM translations WHERE translation_key = p_key) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'TRANSLATION_NOT_FOUND';
  END IF;
  IF p_value IS NULL OR CHAR_LENGTH(TRIM(p_value)) = 0 OR CHAR_LENGTH(p_value) > 4000 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TEXT';
  END IF;
  SELECT value INTO v_old FROM translations WHERE language = p_language AND translation_key = p_key;
  START TRANSACTION;
  INSERT INTO translations (language, translation_key, value, default_value, updated_by)
  VALUES (p_language, p_key, TRIM(p_value), TRIM(p_value), p_actor_id)
  ON DUPLICATE KEY UPDATE value = VALUES(value), updated_at = UTC_TIMESTAMP(3), updated_by = p_actor_id;
  CALL sp_audit_write(p_actor_id, 'translations.save', 'translation', CONCAT(p_language, ':', p_key), CONCAT('Edited text ', p_key, ' (', p_language, ')'),
                      JSON_OBJECT('old', v_old, 'new', TRIM(p_value)), NULL);
  COMMIT;
  SELECT language, translation_key, value, default_value, updated_at FROM translations WHERE language = p_language AND translation_key = p_key;
END$$

CREATE PROCEDURE sp_translations_reset(IN p_actor_id BIGINT UNSIGNED, IN p_language VARCHAR(10), IN p_key VARCHAR(150))
BEGIN
  DECLARE v_default TEXT;
  SELECT default_value INTO v_default FROM translations WHERE language = p_language AND translation_key = p_key;
  IF v_default IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'TRANSLATION_NOT_FOUND';
  END IF;
  CALL sp_translations_save(p_actor_id, p_language, p_key, v_default);
END$$

-- ------------------------------------------------------------------ roles & permissions
CREATE PROCEDURE sp_permissions_list()
BEGIN
  SELECT code, group_name, description FROM permissions ORDER BY group_name, code;
END$$

-- Two result sets: roles (with how many users hold them), then their permission codes. NULL = every role.
CREATE PROCEDURE sp_roles_list(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT r.id, r.name, r.description, (SELECT COUNT(*) FROM user_roles ur WHERE ur.role_id = r.id) AS user_count
  FROM roles r WHERE p_id IS NULL OR r.id = p_id ORDER BY r.name;
  SELECT rp.role_id, rp.permission_code FROM role_permissions rp WHERE p_id IS NULL OR rp.role_id = p_id ORDER BY rp.role_id, rp.permission_code;
END$$

CREATE PROCEDURE sp_roles_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_name VARCHAR(100), IN p_description VARCHAR(300), IN p_permissions JSON)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_bad INT;
  DECLARE EXIT HANDLER FOR 1062
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DUPLICATE_ROLE';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_name IS NULL OR CHAR_LENGTH(TRIM(p_name)) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NAMES';
  END IF;
  IF p_permissions IS NULL OR JSON_TYPE(p_permissions) <> 'ARRAY' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_PERMISSION';
  END IF;
  SELECT COUNT(*) INTO v_bad FROM JSON_TABLE(p_permissions, '$[*]' COLUMNS (code VARCHAR(100) PATH '$')) j
  WHERE j.code IS NULL OR NOT EXISTS (SELECT 1 FROM permissions p WHERE p.code = j.code);
  IF v_bad > 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_PERMISSION';
  END IF;

  START TRANSACTION;
  IF p_id IS NULL THEN
    INSERT INTO roles (name, description) VALUES (TRIM(p_name), p_description);
    SET v_id = LAST_INSERT_ID();
  ELSE
    SELECT id INTO v_id FROM roles WHERE id = p_id FOR UPDATE;
    IF v_id IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROLE_NOT_FOUND';
    END IF;
    UPDATE roles SET name = TRIM(p_name), description = p_description WHERE id = v_id;
    DELETE FROM role_permissions WHERE role_id = v_id;
  END IF;
  INSERT INTO role_permissions (role_id, permission_code)
  SELECT DISTINCT v_id, j.code FROM JSON_TABLE(p_permissions, '$[*]' COLUMNS (code VARCHAR(100) PATH '$')) j;
  CALL sp_audit_write(p_actor_id, IF(p_id IS NULL, 'roles.create', 'roles.update'), 'role', v_id,
                      CONCAT(IF(p_id IS NULL, 'Created role ', 'Updated role '), TRIM(p_name)), JSON_OBJECT('permissions', p_permissions), NULL);
  COMMIT;
  CALL sp_roles_list(v_id);
END$$

CREATE PROCEDURE sp_roles_delete(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_name VARCHAR(100);
  SELECT name INTO v_name FROM roles WHERE id = p_id;
  IF v_name IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROLE_NOT_FOUND';
  END IF;
  IF EXISTS (SELECT 1 FROM user_roles WHERE role_id = p_id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROLE_IN_USE';
  END IF;
  START TRANSACTION;
  DELETE FROM roles WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'roles.delete', 'role', p_id, CONCAT('Deleted role ', v_name), NULL, NULL);
  COMMIT;
END$$

-- Replaces the roles a user holds (p_role_ids is a JSON array of role ids). Returns the user's role names.
CREATE PROCEDURE sp_user_roles_set(IN p_actor_id BIGINT UNSIGNED, IN p_user_id BIGINT UNSIGNED, IN p_role_ids JSON)
BEGIN
  DECLARE v_email VARCHAR(255);
  DECLARE v_bad INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  SELECT email INTO v_email FROM users WHERE id = p_user_id;
  IF v_email IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  IF p_role_ids IS NULL OR JSON_TYPE(p_role_ids) <> 'ARRAY' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROLE_NOT_FOUND';
  END IF;
  SELECT COUNT(*) INTO v_bad FROM JSON_TABLE(p_role_ids, '$[*]' COLUMNS (id BIGINT UNSIGNED PATH '$')) j
  WHERE j.id IS NULL OR NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = j.id);
  IF v_bad > 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ROLE_NOT_FOUND';
  END IF;
  START TRANSACTION;
  DELETE FROM user_roles WHERE user_id = p_user_id;
  INSERT INTO user_roles (user_id, role_id) SELECT DISTINCT p_user_id, j.id FROM JSON_TABLE(p_role_ids, '$[*]' COLUMNS (id BIGINT UNSIGNED PATH '$')) j;
  CALL sp_audit_write(p_actor_id, 'users.roles', 'user', p_user_id, CONCAT('Set roles of ', v_email), JSON_OBJECT('roleIds', p_role_ids), NULL);
  COMMIT;
  SELECT r.id, r.name FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE ur.user_id = p_user_id ORDER BY r.name;
END$$

-- Is this user back-office staff, and what may they do? Two result sets: one row (id, is_super, role_count), then permission codes.
-- A super admin (users.role = 'admin') holds every permission.
CREATE PROCEDURE sp_staff_get(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT u.id, (u.role = 'admin') AS is_super, (SELECT COUNT(*) FROM user_roles ur WHERE ur.user_id = u.id) AS role_count
  FROM users u WHERE u.id = p_user_id AND u.is_locked = 0;

  SELECT p.code AS permission_code FROM permissions p
  WHERE EXISTS (SELECT 1 FROM users u WHERE u.id = p_user_id AND u.is_locked = 0 AND u.role = 'admin')
     OR p.code IN (SELECT rp.permission_code FROM user_roles ur JOIN role_permissions rp ON rp.role_id = ur.role_id
                   JOIN users u2 ON u2.id = ur.user_id AND u2.is_locked = 0 WHERE ur.user_id = p_user_id)
  ORDER BY p.code;
END$$

-- ------------------------------------------------------------------ two-factor (TOTP + recovery codes)
CREATE PROCEDURE sp_twofactor_get(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT secret_protected, (confirmed_at IS NOT NULL) AS is_confirmed, last_used_step FROM user_two_factor WHERE user_id = p_user_id;
END$$

CREATE PROCEDURE sp_twofactor_begin(IN p_user_id BIGINT UNSIGNED, IN p_secret_protected VARCHAR(512))
BEGIN
  IF EXISTS (SELECT 1 FROM user_two_factor WHERE user_id = p_user_id AND confirmed_at IS NOT NULL) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'TWO_FACTOR_ALREADY_ENROLLED';
  END IF;
  INSERT INTO user_two_factor (user_id, secret_protected) VALUES (p_user_id, p_secret_protected)
  ON DUPLICATE KEY UPDATE secret_protected = VALUES(secret_protected), created_at = UTC_TIMESTAMP(3), last_used_step = NULL;
END$$

-- Confirms enrollment after the user proves they can generate a code, and stores the (hashed) recovery codes.
CREATE PROCEDURE sp_twofactor_confirm(IN p_user_id BIGINT UNSIGNED, IN p_step BIGINT, IN p_hashes JSON)
BEGIN
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF NOT EXISTS (SELECT 1 FROM user_two_factor WHERE user_id = p_user_id AND confirmed_at IS NULL) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'TWO_FACTOR_NOT_STARTED';
  END IF;
  START TRANSACTION;
  UPDATE user_two_factor SET confirmed_at = UTC_TIMESTAMP(3), last_used_step = p_step WHERE user_id = p_user_id;
  DELETE FROM user_recovery_codes WHERE user_id = p_user_id;
  INSERT INTO user_recovery_codes (user_id, code_hash) SELECT p_user_id, j.h FROM JSON_TABLE(p_hashes, '$[*]' COLUMNS (h CHAR(64) PATH '$')) j;
  CALL sp_audit_write(p_user_id, 'admin.2fa.enrolled', 'user', p_user_id, 'Two-factor sign-in enrolled', NULL, NULL);
  COMMIT;
END$$

-- A time step may be used once (replay protection).
CREATE PROCEDURE sp_twofactor_use_step(IN p_user_id BIGINT UNSIGNED, IN p_step BIGINT)
BEGIN
  UPDATE user_two_factor SET last_used_step = p_step
   WHERE user_id = p_user_id AND confirmed_at IS NOT NULL AND (last_used_step IS NULL OR last_used_step < p_step);
  IF ROW_COUNT() = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'TOTP_REPLAY';
  END IF;
END$$

CREATE PROCEDURE sp_twofactor_use_recovery(IN p_user_id BIGINT UNSIGNED, IN p_hash CHAR(64))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM user_recovery_codes WHERE user_id = p_user_id AND code_hash = p_hash AND used_at IS NULL LIMIT 1 FOR UPDATE;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_RECOVERY_CODE';
  END IF;
  UPDATE user_recovery_codes SET used_at = UTC_TIMESTAMP(3) WHERE id = v_id;
  CALL sp_audit_write(p_user_id, 'admin.recovery_code.used', 'user', p_user_id, 'A recovery code was used to sign in', NULL, NULL);
END$$

CREATE PROCEDURE sp_twofactor_replace_recovery(IN p_user_id BIGINT UNSIGNED, IN p_hashes JSON)
BEGIN
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  DELETE FROM user_recovery_codes WHERE user_id = p_user_id;
  INSERT INTO user_recovery_codes (user_id, code_hash) SELECT p_user_id, j.h FROM JSON_TABLE(p_hashes, '$[*]' COLUMNS (h CHAR(64) PATH '$')) j;
  CALL sp_audit_write(p_user_id, 'admin.recovery_codes.regenerated', 'user', p_user_id, 'Recovery codes were regenerated', NULL, NULL);
  COMMIT;
END$$

CREATE PROCEDURE sp_twofactor_recovery_count(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT COUNT(*) AS remaining FROM user_recovery_codes WHERE user_id = p_user_id AND used_at IS NULL;
END$$

-- Another super admin clears a colleague's two-factor setup (lost device); they enroll again at next sign-in.
CREATE PROCEDURE sp_twofactor_reset(IN p_actor_id BIGINT UNSIGNED, IN p_user_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_email VARCHAR(255);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  SELECT email INTO v_email FROM users WHERE id = p_user_id;
  IF v_email IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  START TRANSACTION;
  DELETE FROM user_recovery_codes WHERE user_id = p_user_id;
  DELETE FROM user_two_factor WHERE user_id = p_user_id;
  UPDATE users SET failed_login_attempts = 0, locked_until = NULL WHERE id = p_user_id;
  CALL sp_audit_write(p_actor_id, 'admin.2fa.reset', 'user', p_user_id, CONCAT('Reset two-factor sign-in of ', v_email), NULL, NULL);
  COMMIT;
END$$

-- Break-glass: used by the migrator command line when no admin can sign in any more (every device and recovery code lost).
CREATE PROCEDURE sp_twofactor_reset_by_email(IN p_email VARCHAR(255))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM users WHERE email = p_email;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  CALL sp_twofactor_reset(NULL, v_id);
END$$

-- ------------------------------------------------------------------ catalog management
CREATE PROCEDURE sp_catalog_categories_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_name VARCHAR(100), IN p_name_ar VARCHAR(100),
  IN p_description TEXT, IN p_description_ar TEXT, IN p_icon VARCHAR(50), IN p_is_blocked TINYINT(1))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 1062
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DUPLICATE_NAME';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_name IS NULL OR CHAR_LENGTH(TRIM(p_name)) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NAMES';
  END IF;
  START TRANSACTION;
  IF p_id IS NULL THEN
    INSERT INTO categories (name, name_ar, description, description_ar, icon, is_blocked)
    VALUES (TRIM(p_name), p_name_ar, p_description, p_description_ar, p_icon, IFNULL(p_is_blocked, 0));
    SET v_id = LAST_INSERT_ID();
  ELSE
    SELECT id INTO v_id FROM categories WHERE id = p_id FOR UPDATE;
    IF v_id IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CATEGORY_NOT_FOUND';
    END IF;
    UPDATE categories SET name = TRIM(p_name), name_ar = p_name_ar, description = p_description, description_ar = p_description_ar,
           icon = p_icon, is_blocked = IFNULL(p_is_blocked, is_blocked) WHERE id = v_id;
  END IF;
  CALL sp_audit_write(p_actor_id, IF(p_id IS NULL, 'catalog.category.create', 'catalog.category.update'), 'category', v_id,
                      CONCAT(IF(p_id IS NULL, 'Created category ', 'Edited category '), TRIM(p_name), IF(IFNULL(p_is_blocked, 0) = 1, ' (blocked)', '')), NULL, NULL);
  COMMIT;
  SELECT id, name, name_ar, description, description_ar, icon, is_blocked FROM categories WHERE id = v_id;
END$$

CREATE PROCEDURE sp_catalog_services_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_category_id BIGINT UNSIGNED, IN p_name VARCHAR(100), IN p_name_ar VARCHAR(100),
  IN p_description TEXT, IN p_description_ar TEXT, IN p_is_blocked TINYINT(1))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 1062
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DUPLICATE_NAME';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_name IS NULL OR CHAR_LENGTH(TRIM(p_name)) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NAMES';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM categories WHERE id = p_category_id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CATEGORY_NOT_FOUND';
  END IF;
  START TRANSACTION;
  IF p_id IS NULL THEN
    INSERT INTO services (category_id, name, name_ar, description, description_ar, is_blocked)
    VALUES (p_category_id, TRIM(p_name), p_name_ar, p_description, p_description_ar, IFNULL(p_is_blocked, 0));
    SET v_id = LAST_INSERT_ID();
  ELSE
    SELECT id INTO v_id FROM services WHERE id = p_id FOR UPDATE;
    IF v_id IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SERVICE_NOT_FOUND';
    END IF;
    UPDATE services SET category_id = p_category_id, name = TRIM(p_name), name_ar = p_name_ar, description = p_description,
           description_ar = p_description_ar, is_blocked = IFNULL(p_is_blocked, is_blocked) WHERE id = v_id;
  END IF;
  CALL sp_audit_write(p_actor_id, IF(p_id IS NULL, 'catalog.service.create', 'catalog.service.update'), 'service', v_id,
                      CONCAT(IF(p_id IS NULL, 'Created service ', 'Edited service '), TRIM(p_name), IF(IFNULL(p_is_blocked, 0) = 1, ' (blocked)', '')), NULL, NULL);
  COMMIT;
  SELECT id, category_id, name, name_ar, description, description_ar, is_blocked FROM services WHERE id = v_id;
END$$

DELIMITER ;
