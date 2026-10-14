-- Repeatable: user procedures. Re-applied by the migrator whenever this file changes.
-- Errors are raised with SIGNAL SQLSTATE '45000'; MESSAGE_TEXT is a stable error code
-- that the C# data layer maps to a domain error.

DROP PROCEDURE IF EXISTS sp_health_ping;
DROP PROCEDURE IF EXISTS sp_users_get_by_id;
DROP PROCEDURE IF EXISTS sp_users_get_by_email;
DROP PROCEDURE IF EXISTS sp_users_create_local;
DROP PROCEDURE IF EXISTS sp_users_record_login_failure;
DROP PROCEDURE IF EXISTS sp_users_record_login_success;

DELIMITER $$

CREATE PROCEDURE sp_health_ping()
BEGIN
  SELECT 1 AS ok;
END$$

CREATE PROCEDURE sp_users_get_by_id(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT id, email, email_verified, password_hash, first_name, last_name, role, profile_type,
         is_locked, failed_login_attempts, locked_until, created_at
  FROM users
  WHERE id = p_id;
END$$

CREATE PROCEDURE sp_users_get_by_email(IN p_email VARCHAR(255))
BEGIN
  SELECT id, email, email_verified, password_hash, first_name, last_name, role, profile_type,
         is_locked, failed_login_attempts, locked_until, created_at
  FROM users
  WHERE email = p_email;
END$$

CREATE PROCEDURE sp_users_create_local(
  IN p_email VARCHAR(255), IN p_password_hash VARCHAR(255),
  IN p_first_name VARCHAR(100), IN p_last_name VARCHAR(100))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 1062
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DUPLICATE_EMAIL';

  INSERT INTO users (email, email_verified, password_hash, first_name, last_name)
  VALUES (p_email, 0, p_password_hash, p_first_name, p_last_name);
  SET v_id = LAST_INSERT_ID();
  CALL sp_users_get_by_id(v_id);
END$$

-- Counts a failed password attempt and locks the account for p_lock_minutes
-- once p_max_attempts consecutive failures are reached.
CREATE PROCEDURE sp_users_record_login_failure(
  IN p_id BIGINT UNSIGNED, IN p_max_attempts INT, IN p_lock_minutes INT)
BEGIN
  DECLARE v_attempts INT UNSIGNED;
  START TRANSACTION;
  SELECT failed_login_attempts + 1 INTO v_attempts FROM users WHERE id = p_id FOR UPDATE;
  IF v_attempts IS NOT NULL THEN
    UPDATE users
       SET failed_login_attempts = v_attempts,
           locked_until = IF(v_attempts >= p_max_attempts,
                             DATE_ADD(UTC_TIMESTAMP(3), INTERVAL p_lock_minutes MINUTE),
                             locked_until)
     WHERE id = p_id;
  END IF;
  COMMIT;
END$$

CREATE PROCEDURE sp_users_record_login_success(IN p_id BIGINT UNSIGNED)
BEGIN
  UPDATE users
     SET failed_login_attempts = 0, locked_until = NULL, last_signed_in_at = UTC_TIMESTAMP(3)
   WHERE id = p_id;
END$$

DELIMITER ;
