-- Repeatable: external identity (Google) procedures.

DROP PROCEDURE IF EXISTS sp_identities_find_user;
DROP PROCEDURE IF EXISTS sp_identities_create_user;
DROP PROCEDURE IF EXISTS sp_identities_link;

DELIMITER $$

CREATE PROCEDURE sp_identities_find_user(IN p_provider VARCHAR(30), IN p_subject VARCHAR(255))
BEGIN
  SELECT u.id, u.email, u.email_verified, u.password_hash, u.first_name, u.last_name, u.role,
         u.profile_type, u.is_locked, u.failed_login_attempts, u.locked_until, u.created_at, u.is_premium, u.is_starred
  FROM user_identities i
  JOIN users u ON u.id = i.user_id
  WHERE i.provider = p_provider AND i.subject = p_subject;
END$$

-- Creates a brand-new passwordless user together with its identity.
-- Fails with DUPLICATE_ACCOUNT if the email already belongs to another account:
-- the caller must NOT silently merge; it has to run the password-proof link flow.
CREATE PROCEDURE sp_identities_create_user(
  IN p_provider VARCHAR(30), IN p_subject VARCHAR(255), IN p_email VARCHAR(255),
  IN p_email_verified TINYINT(1), IN p_first_name VARCHAR(100), IN p_last_name VARCHAR(100))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 1062
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DUPLICATE_ACCOUNT';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  START TRANSACTION;
  INSERT INTO users (email, email_verified, password_hash, first_name, last_name)
  VALUES (p_email, p_email_verified, NULL, p_first_name, p_last_name);
  SET v_id = LAST_INSERT_ID();
  INSERT INTO user_identities (user_id, provider, subject, email, email_verified)
  VALUES (v_id, p_provider, p_subject, p_email, p_email_verified);
  COMMIT;

  CALL sp_users_get_by_id(v_id);
END$$

-- Attaches an identity to an existing user. If the provider vouches for the same
-- address the account already has, the account's email becomes verified.
CREATE PROCEDURE sp_identities_link(
  IN p_user_id BIGINT UNSIGNED, IN p_provider VARCHAR(30), IN p_subject VARCHAR(255),
  IN p_email VARCHAR(255), IN p_email_verified TINYINT(1))
BEGIN
  DECLARE EXIT HANDLER FOR 1062
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'IDENTITY_ALREADY_LINKED';
  END;
  DECLARE EXIT HANDLER FOR 1452
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  START TRANSACTION;
  INSERT INTO user_identities (user_id, provider, subject, email, email_verified)
  VALUES (p_user_id, p_provider, p_subject, p_email, p_email_verified);
  IF p_email_verified = 1 THEN
    UPDATE users SET email_verified = 1 WHERE id = p_user_id AND email = p_email;
  END IF;
  COMMIT;

  CALL sp_users_get_by_id(p_user_id);
END$$

DELIMITER ;
