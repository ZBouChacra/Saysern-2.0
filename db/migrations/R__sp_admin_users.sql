-- Repeatable: back-office user administration.

DROP PROCEDURE IF EXISTS sp_admin_users_list;
DROP PROCEDURE IF EXISTS sp_admin_users_set_locked;
DROP PROCEDURE IF EXISTS sp_admin_users_set_role;
DROP PROCEDURE IF EXISTS sp_admin_users_promote_by_email;
DROP PROCEDURE IF EXISTS sp_admin_users_set_flags;

DELIMITER $$

-- Two result sets: the requested page, then a single row with the total match count.
CREATE PROCEDURE sp_admin_users_list(IN p_search VARCHAR(255), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(300);

  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 20), 1), 100);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = NULL;
  IF p_search IS NOT NULL AND CHAR_LENGTH(TRIM(p_search)) > 0 THEN
    SET v_like = CONCAT('%', TRIM(p_search), '%');
  END IF;

  SELECT id, email, email_verified, password_hash, first_name, last_name, role, profile_type,
         is_locked, failed_login_attempts, locked_until, created_at, is_premium, is_starred
  FROM users
  WHERE v_like IS NULL OR email LIKE v_like OR first_name LIKE v_like OR last_name LIKE v_like
  ORDER BY created_at DESC, id DESC
  LIMIT v_size OFFSET v_offset;

  SELECT COUNT(*) AS total
  FROM users
  WHERE v_like IS NULL OR email LIKE v_like OR first_name LIKE v_like OR last_name LIKE v_like;
END$$

-- Locks/unlocks an account. Refuses to lock the last active admin.
CREATE PROCEDURE sp_admin_users_set_locked(IN p_user_id BIGINT UNSIGNED, IN p_is_locked TINYINT(1))
BEGIN
  DECLARE v_role VARCHAR(10);
  DECLARE v_others INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  START TRANSACTION;
  SELECT role INTO v_role FROM users WHERE id = p_user_id FOR UPDATE;
  IF v_role IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  IF v_role = 'admin' AND p_is_locked = 1 THEN
    SELECT COUNT(*) INTO v_others FROM users WHERE role = 'admin' AND is_locked = 0 AND id <> p_user_id;
    IF v_others = 0 THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'LAST_ADMIN';
    END IF;
  END IF;
  UPDATE users
     SET is_locked = p_is_locked,
         failed_login_attempts = IF(p_is_locked = 0, 0, failed_login_attempts),
         locked_until = IF(p_is_locked = 0, NULL, locked_until)
   WHERE id = p_user_id;
  COMMIT;

  CALL sp_users_get_by_id(p_user_id);
END$$

-- Changes a role. Refuses to demote the last active admin.
CREATE PROCEDURE sp_admin_users_set_role(IN p_user_id BIGINT UNSIGNED, IN p_role VARCHAR(10))
BEGIN
  DECLARE v_current VARCHAR(10);
  DECLARE v_others INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  IF p_role IS NULL OR p_role NOT IN ('user', 'admin') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ROLE';
  END IF;

  START TRANSACTION;
  SELECT role INTO v_current FROM users WHERE id = p_user_id FOR UPDATE;
  IF v_current IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  IF v_current = 'admin' AND p_role = 'user' THEN
    SELECT COUNT(*) INTO v_others FROM users WHERE role = 'admin' AND is_locked = 0 AND id <> p_user_id;
    IF v_others = 0 THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'LAST_ADMIN';
    END IF;
  END IF;
  UPDATE users SET role = p_role WHERE id = p_user_id;
  COMMIT;

  CALL sp_users_get_by_id(p_user_id);
END$$

-- Used by the migrator CLI to create the first admin.
CREATE PROCEDURE sp_admin_users_promote_by_email(IN p_email VARCHAR(255))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM users WHERE email = p_email;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  UPDATE users SET role = 'admin' WHERE id = v_id;
  CALL sp_users_get_by_id(v_id);
END$$

-- Premium / starred listing flags (affect search ranking).
CREATE PROCEDURE sp_admin_users_set_flags(IN p_user_id BIGINT UNSIGNED, IN p_is_premium TINYINT(1), IN p_is_starred TINYINT(1))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM users WHERE id = p_user_id;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  UPDATE users SET is_premium = p_is_premium, is_starred = p_is_starred WHERE id = p_user_id;
  CALL sp_users_get_by_id(p_user_id);
END$$

DELIMITER ;
