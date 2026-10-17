-- Repeatable: preferences, password change, disabling and re-enabling an account.

DROP PROCEDURE IF EXISTS sp_users_update_preferences;
DROP PROCEDURE IF EXISTS sp_users_change_password;
DROP PROCEDURE IF EXISTS sp_users_set_disabled;
DROP PROCEDURE IF EXISTS sp_account_reenable;

DELIMITER $$

CREATE PROCEDURE sp_users_update_preferences(IN p_user_id BIGINT UNSIGNED, IN p_language VARCHAR(10), IN p_time_zone VARCHAR(60))
BEGIN
  IF p_language IS NULL OR NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'language' AND code = p_language AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_LANGUAGE';
  END IF;
  IF p_time_zone IS NOT NULL AND CHAR_LENGTH(p_time_zone) > 0 AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'time_zone' AND code = p_time_zone AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TIME_ZONE';
  END IF;
  UPDATE users SET preferred_language = p_language, time_zone = NULLIF(p_time_zone, '') WHERE id = p_user_id;
  CALL sp_users_get_by_id(p_user_id);
END$$

-- The new hash is computed by the API (after checking the current password and the password rules). A temporary lockout is cleared.
CREATE PROCEDURE sp_users_change_password(IN p_user_id BIGINT UNSIGNED, IN p_password_hash VARCHAR(255))
BEGIN
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  UPDATE users SET password_hash = p_password_hash, failed_login_attempts = 0, locked_until = NULL WHERE id = p_user_id;
  CALL sp_audit_write(p_user_id, 'account.password', 'user', p_user_id, 'Changed their password', NULL, NULL);
  COMMIT;
  CALL sp_users_get_by_id(p_user_id);
END$$

-- A person disables (or, by confirming an e-mailed link, re-enables) their own account. Nothing is deleted.
CREATE PROCEDURE sp_users_set_disabled(IN p_user_id BIGINT UNSIGNED, IN p_disabled TINYINT(1))
BEGIN
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  UPDATE users SET is_disabled = p_disabled, disabled_at = IF(p_disabled = 1, UTC_TIMESTAMP(3), NULL) WHERE id = p_user_id;
  CALL sp_audit_write(p_user_id, IF(p_disabled = 1, 'account.disable', 'account.reenable'), 'user', p_user_id, IF(p_disabled = 1, 'Disabled their account', 'Re-enabled their account'), NULL, NULL);
  COMMIT;
  CALL sp_users_get_by_id(p_user_id);
END$$

-- Confirms a re-enable link (single use, must be unexpired). An account that STAFF locked can never be re-enabled this way.
CREATE PROCEDURE sp_account_reenable(IN p_token_hash CHAR(64))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_user BIGINT UNSIGNED;
  DECLARE v_locked TINYINT;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  SELECT id, user_id INTO v_id, v_user FROM user_tokens
   WHERE token_hash = p_token_hash AND purpose = 'reenable_account' AND used_at IS NULL AND expires_at > UTC_TIMESTAMP(3) FOR UPDATE;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TOKEN';
  END IF;
  SELECT is_locked INTO v_locked FROM users WHERE id = v_user FOR UPDATE;
  IF v_locked = 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ACCOUNT_LOCKED';
  END IF;
  UPDATE user_tokens SET used_at = UTC_TIMESTAMP(3) WHERE id = v_id;
  UPDATE users SET is_disabled = 0, disabled_at = NULL, email_verified = 1 WHERE id = v_user;
  CALL sp_audit_write(v_user, 'account.reenable', 'user', v_user, 'Re-enabled their account (confirmed by e-mail)', NULL, NULL);
  COMMIT;
  SELECT v_user AS user_id;
END$$

DELIMITER ;
