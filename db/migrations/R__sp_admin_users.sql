-- Repeatable: back-office user administration. Every change takes the acting admin's id and is audited in the same transaction.

DROP PROCEDURE IF EXISTS sp_admin_users_list;
DROP PROCEDURE IF EXISTS sp_admin_users_set_locked;
DROP PROCEDURE IF EXISTS sp_admin_users_set_role;
DROP PROCEDURE IF EXISTS sp_admin_users_promote_by_email;
DROP PROCEDURE IF EXISTS sp_admin_users_set_flags;

DELIMITER $$

-- Two result sets: the requested page (with role names and whether two-factor is set up), then the total match count.
CREATE PROCEDURE sp_admin_users_list(IN p_search VARCHAR(255), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(300);

  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 20), 1), 100);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = NULL;
  IF p_search IS NOT NULL AND CHAR_LENGTH(TRIM(p_search)) > 0 THEN
    SET v_like = CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_search), '|', '||'), '%', '|%'), '_', '|_'), '%');
  END IF;

  SELECT u.id, u.email, u.email_verified, u.password_hash, u.first_name, u.last_name, u.role, u.profile_type,
         u.is_locked, u.failed_login_attempts, u.locked_until, u.created_at, EXISTS (SELECT 1 FROM premium_days pdx JOIN professions prx ON prx.id = pdx.profession_id WHERE prx.user_id = u.id AND pdx.day = UTC_DATE()) AS is_premium, u.is_starred,
         (SELECT GROUP_CONCAT(r.name ORDER BY r.name SEPARATOR ', ') FROM user_roles ur JOIN roles r ON r.id = ur.role_id WHERE ur.user_id = u.id) AS role_names,
         EXISTS (SELECT 1 FROM user_two_factor t WHERE t.user_id = u.id AND t.confirmed_at IS NOT NULL) AS has_two_factor
  FROM users u
  WHERE v_like IS NULL OR u.email LIKE v_like ESCAPE '|' OR u.first_name LIKE v_like ESCAPE '|' OR u.last_name LIKE v_like ESCAPE '|'
  ORDER BY u.created_at DESC, u.id DESC
  LIMIT v_size OFFSET v_offset;

  SELECT COUNT(*) AS total
  FROM users u
  WHERE v_like IS NULL OR u.email LIKE v_like ESCAPE '|' OR u.first_name LIKE v_like ESCAPE '|' OR u.last_name LIKE v_like ESCAPE '|';
END$$

-- Locks/unlocks an account. Refuses to lock the last active admin.
CREATE PROCEDURE sp_admin_users_set_locked(IN p_actor_id BIGINT UNSIGNED, IN p_user_id BIGINT UNSIGNED, IN p_is_locked TINYINT(1))
BEGIN
  DECLARE v_role VARCHAR(10);
  DECLARE v_email VARCHAR(255);
  DECLARE v_others INT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  START TRANSACTION;
  SELECT role, email INTO v_role, v_email FROM users WHERE id = p_user_id FOR UPDATE;
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
  CALL sp_audit_write(p_actor_id, IF(p_is_locked = 1, 'users.lock', 'users.unlock'), 'user', p_user_id,
                      CONCAT(IF(p_is_locked = 1, 'Locked ', 'Unlocked '), v_email), NULL, NULL);
  COMMIT;

  CALL sp_users_get_by_id(p_user_id);
END$$

-- Changes the super-admin flag. Refuses to demote the last active admin.
CREATE PROCEDURE sp_admin_users_set_role(IN p_actor_id BIGINT UNSIGNED, IN p_user_id BIGINT UNSIGNED, IN p_role VARCHAR(10))
BEGIN
  DECLARE v_current VARCHAR(10);
  DECLARE v_email VARCHAR(255);
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
  SELECT role, email INTO v_current, v_email FROM users WHERE id = p_user_id FOR UPDATE;
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
  CALL sp_audit_write(p_actor_id, 'users.role', 'user', p_user_id, CONCAT('Set super-admin role of ', v_email, ' to ', p_role),
                      JSON_OBJECT('old', v_current, 'new', p_role), NULL);
  COMMIT;

  CALL sp_users_get_by_id(p_user_id);
END$$

-- Used by the migrator CLI to create the first admin (no acting user).
CREATE PROCEDURE sp_admin_users_promote_by_email(IN p_email VARCHAR(255))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM users WHERE email = p_email;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  UPDATE users SET role = 'admin' WHERE id = v_id;
  CALL sp_audit_write(NULL, 'cli.promote_admin', 'user', v_id, CONCAT('Promoted ', p_email, ' to super admin from the command line'), NULL, NULL);
  CALL sp_users_get_by_id(v_id);
END$$

-- The starred (featured) flag, which affects search ranking. Premium is not a flag any more: it comes from paid premium batches.
CREATE PROCEDURE sp_admin_users_set_flags(IN p_actor_id BIGINT UNSIGNED, IN p_user_id BIGINT UNSIGNED, IN p_is_starred TINYINT(1))
BEGIN
  DECLARE v_email VARCHAR(255);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  SELECT email INTO v_email FROM users WHERE id = p_user_id FOR UPDATE;
  IF v_email IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  UPDATE users SET is_starred = p_is_starred WHERE id = p_user_id;
  CALL sp_audit_write(p_actor_id, 'users.flags', 'user', p_user_id, CONCAT('Set featured flag of ', v_email, ' to ', p_is_starred),
                      JSON_OBJECT('starred', p_is_starred), NULL);
  COMMIT;
  CALL sp_users_get_by_id(p_user_id);
END$$

DELIMITER ;
