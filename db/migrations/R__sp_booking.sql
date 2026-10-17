-- Repeatable: appointments and reviews.
-- The booking state machine and review eligibility are enforced here, not in the API, so no caller can bypass them.
-- All instants are UTC.

DROP PROCEDURE IF EXISTS sp_appointments_get;
DROP PROCEDURE IF EXISTS sp_appointments_create;
DROP PROCEDURE IF EXISTS sp_appointments_list_by_user;
DROP PROCEDURE IF EXISTS sp_appointments_transition;
DROP PROCEDURE IF EXISTS sp_appointments_busy;
DROP PROCEDURE IF EXISTS sp_reviews_create;
DROP PROCEDURE IF EXISTS sp_reviews_list_by_professional;
DROP PROCEDURE IF EXISTS sp_reviews_eligible;

DELIMITER $$

CREATE PROCEDURE sp_appointments_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT a.id, a.client_id, a.professional_id, a.profession_id, a.service_id, a.starts_at, a.ends_at, a.duration_minutes,
         a.description, a.status, a.created_at,
         cl.first_name AS client_first_name, cl.last_name AS client_last_name, cl.profile_photo AS client_photo,
         pr.first_name AS professional_first_name, pr.last_name AS professional_last_name, pr.profile_photo AS professional_photo,
         COALESCE(s.name, ps.name) AS service_name, c.name AS category_name,
         EXISTS (SELECT 1 FROM reviews r WHERE r.appointment_id = a.id) AS has_review
  FROM appointments a
  JOIN users cl ON cl.id = a.client_id
  JOIN users pr ON pr.id = a.professional_id
  LEFT JOIN professions p ON p.id = a.profession_id
  LEFT JOIN services s ON s.id = a.service_id
  LEFT JOIN services ps ON ps.id = p.service_id
  LEFT JOIN categories c ON c.id = COALESCE(p.category_id, s.category_id)
  WHERE a.id = p_id;
END$$

-- Books a slot. Serialised per professional (row lock) so two clients cannot grab the same time.
-- Back-to-back bookings are allowed (an appointment ending at 11:00 does not block one starting at 11:00).
CREATE PROCEDURE sp_appointments_create(
  IN p_client_id BIGINT UNSIGNED, IN p_professional_id BIGINT UNSIGNED, IN p_profession_id BIGINT UNSIGNED,
  IN p_starts_at DATETIME(3), IN p_duration_minutes INT, IN p_description TEXT)
BEGIN
  DECLARE v_ends DATETIME(3);
  DECLARE v_type VARCHAR(20);
  DECLARE v_locked TINYINT(1);
  DECLARE v_service BIGINT UNSIGNED;
  DECLARE v_busy BIGINT UNSIGNED;
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF (SELECT value_text FROM app_settings WHERE setting_key = 'Auth.RequireVerifiedEmail') = 'true'
     AND (SELECT value_text FROM app_settings WHERE setting_key = 'Email.Provider') <> 'off'
     AND NOT EXISTS (SELECT 1 FROM users WHERE id = p_client_id AND email_verified = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'EMAIL_NOT_VERIFIED';
  END IF;

  IF p_client_id = p_professional_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'CANNOT_BOOK_SELF';
  END IF;
  IF p_duration_minutes IS NULL OR p_duration_minutes < (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Booking.MinDurationMinutes') OR p_duration_minutes > (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Booking.MaxDurationMinutes') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DURATION';
  END IF;
  IF p_starts_at IS NULL OR p_starts_at <= UTC_TIMESTAMP(3) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'START_IN_PAST';
  END IF;
  SET v_ends = DATE_ADD(p_starts_at, INTERVAL p_duration_minutes MINUTE);

  START TRANSACTION;
  SELECT profile_type, is_locked INTO v_type, v_locked FROM users WHERE id = p_professional_id FOR UPDATE;
  IF v_type IS NULL OR v_type <> 'professional' OR v_locked = 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PROFESSIONAL_NOT_FOUND';
  END IF;
  IF p_profession_id IS NOT NULL THEN
    SELECT service_id INTO v_service FROM professions
     WHERE id = p_profession_id AND user_id = p_professional_id AND is_hidden = 0 AND is_locked = 0;
    IF v_service IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PROFESSION_NOT_FOUND';
    END IF;
  END IF;

  SELECT id INTO v_busy FROM appointments
   WHERE professional_id = p_professional_id AND status IN ('pending', 'approved')
     AND starts_at < v_ends AND ends_at > p_starts_at
   LIMIT 1;
  IF v_busy IS NOT NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SLOT_TAKEN';
  END IF;

  INSERT INTO appointments (client_id, professional_id, profession_id, service_id, starts_at, ends_at, duration_minutes, description)
  VALUES (p_client_id, p_professional_id, p_profession_id, v_service, p_starts_at, v_ends, p_duration_minutes, p_description);
  SET v_id = LAST_INSERT_ID();
  COMMIT;

  CALL sp_appointments_get(v_id);
END$$

-- Appointments where the user is the client or the professional, newest first.
CREATE PROCEDURE sp_appointments_list_by_user(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT a.id, a.client_id, a.professional_id, a.profession_id, a.service_id, a.starts_at, a.ends_at, a.duration_minutes,
         a.description, a.status, a.created_at,
         cl.first_name AS client_first_name, cl.last_name AS client_last_name, cl.profile_photo AS client_photo,
         pr.first_name AS professional_first_name, pr.last_name AS professional_last_name, pr.profile_photo AS professional_photo,
         COALESCE(s.name, ps.name) AS service_name, c.name AS category_name,
         EXISTS (SELECT 1 FROM reviews r WHERE r.appointment_id = a.id) AS has_review
  FROM appointments a
  JOIN users cl ON cl.id = a.client_id
  JOIN users pr ON pr.id = a.professional_id
  LEFT JOIN professions p ON p.id = a.profession_id
  LEFT JOIN services s ON s.id = a.service_id
  LEFT JOIN services ps ON ps.id = p.service_id
  LEFT JOIN categories c ON c.id = COALESCE(p.category_id, s.category_id)
  WHERE a.client_id = p_user_id OR a.professional_id = p_user_id
  ORDER BY a.starts_at DESC, a.id DESC;
END$$

-- State machine (actor must be the client or the professional; anyone else just gets "not found"):
--   pending   -> approved   professional only
--   pending   -> cancelled  either party
--   approved  -> cancelled  either party
--   approved  -> completed  professional only, and only once the appointment has started
-- completed and cancelled are final.
CREATE PROCEDURE sp_appointments_transition(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_status VARCHAR(20))
BEGIN
  DECLARE v_status VARCHAR(20);
  DECLARE v_client BIGINT UNSIGNED;
  DECLARE v_pro BIGINT UNSIGNED;
  DECLARE v_starts DATETIME(3);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  IF p_status IS NULL OR p_status NOT IN ('approved', 'cancelled', 'completed') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_STATUS';
  END IF;

  START TRANSACTION;
  SELECT status, client_id, professional_id, starts_at INTO v_status, v_client, v_pro, v_starts
    FROM appointments WHERE id = p_id FOR UPDATE;
  IF v_status IS NULL OR (p_actor_id <> v_client AND p_actor_id <> v_pro) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'APPOINTMENT_NOT_FOUND';
  END IF;

  IF p_status = 'approved' THEN
    IF p_actor_id <> v_pro THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FORBIDDEN_ACTION';
    END IF;
    IF v_status <> 'pending' THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TRANSITION';
    END IF;
  ELSEIF p_status = 'cancelled' THEN
    IF v_status NOT IN ('pending', 'approved') THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TRANSITION';
    END IF;
  ELSE
    IF p_actor_id <> v_pro THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FORBIDDEN_ACTION';
    END IF;
    IF v_status <> 'approved' THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TRANSITION';
    END IF;
    IF v_starts > UTC_TIMESTAMP(3) THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_STARTED_YET';
    END IF;
  END IF;

  UPDATE appointments SET status = p_status, updated_at = UTC_TIMESTAMP(3) WHERE id = p_id;
  COMMIT;
  CALL sp_appointments_get(p_id);
END$$

-- Public: only WHEN a professional is busy, never who booked or why.
CREATE PROCEDURE sp_appointments_busy(IN p_professional_id BIGINT UNSIGNED, IN p_from DATETIME(3), IN p_to DATETIME(3))
BEGIN
  DECLARE v_from DATETIME(3);
  DECLARE v_to DATETIME(3);
  SET v_from = IFNULL(p_from, UTC_TIMESTAMP(3));
  SET v_to = IFNULL(p_to, DATE_ADD(v_from, INTERVAL 90 DAY));
  SELECT starts_at, ends_at
  FROM appointments
  WHERE professional_id = p_professional_id AND status IN ('pending', 'approved')
    AND ends_at > v_from AND starts_at < v_to
  ORDER BY starts_at
  LIMIT 500;
END$$

-- Only the appointment's own client, only after it was completed, only once. The professional is taken
-- from the appointment, never from the caller. The professional's rating is recomputed in the same transaction.
CREATE PROCEDURE sp_reviews_create(IN p_reviewer_id BIGINT UNSIGNED, IN p_appointment_id BIGINT UNSIGNED, IN p_rating INT, IN p_comment TEXT)
BEGIN
  DECLARE v_client BIGINT UNSIGNED;
  DECLARE v_pro BIGINT UNSIGNED;
  DECLARE v_profession BIGINT UNSIGNED;
  DECLARE v_status VARCHAR(20);
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 1062
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REVIEW_EXISTS';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  IF p_rating IS NULL OR p_rating < 1 OR p_rating > 5 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_RATING';
  END IF;

  START TRANSACTION;
  SELECT client_id, professional_id, profession_id, status INTO v_client, v_pro, v_profession, v_status
    FROM appointments WHERE id = p_appointment_id FOR UPDATE;
  IF v_client IS NULL OR v_client <> p_reviewer_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'APPOINTMENT_NOT_FOUND';
  END IF;
  IF v_status <> 'completed' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_COMPLETED';
  END IF;

  INSERT INTO reviews (appointment_id, reviewer_id, professional_id, profession_id, rating, comment)
  VALUES (p_appointment_id, p_reviewer_id, v_pro, v_profession, p_rating, p_comment);
  SET v_id = LAST_INSERT_ID();

  UPDATE professions
     SET avg_rating = (SELECT IFNULL(AVG(rating), 0) FROM reviews WHERE professional_id = v_pro AND is_deleted = 0),
         total_reviews = (SELECT COUNT(*) FROM reviews WHERE professional_id = v_pro AND is_deleted = 0)
   WHERE user_id = v_pro;
  COMMIT;

  SELECT id, appointment_id, professional_id, rating, comment, created_at FROM reviews WHERE id = v_id;
END$$

CREATE PROCEDURE sp_reviews_list_by_professional(IN p_professional_id BIGINT UNSIGNED, IN p_limit INT, IN p_offset INT)
BEGIN
  DECLARE v_limit INT;
  DECLARE v_offset INT;
  SET v_limit = LEAST(GREATEST(IFNULL(p_limit, 50), 1), 100);
  SET v_offset = GREATEST(IFNULL(p_offset, 0), 0);
  SELECT r.id, r.rating, r.comment, r.created_at, u.first_name AS reviewer_first_name, u.last_name AS reviewer_last_name,
         u.profile_photo AS reviewer_photo, r.reply_text, r.replied_at, r.reply_edited_at
  FROM reviews r
  JOIN users u ON u.id = r.reviewer_id
  WHERE r.professional_id = p_professional_id AND r.is_deleted = 0
  ORDER BY r.created_at DESC, r.id DESC
  LIMIT v_limit OFFSET v_offset;
END$$

-- The caller's completed, not-yet-reviewed appointments with this professional.
CREATE PROCEDURE sp_reviews_eligible(IN p_client_id BIGINT UNSIGNED, IN p_professional_id BIGINT UNSIGNED)
BEGIN
  SELECT a.id, a.starts_at
  FROM appointments a
  WHERE a.client_id = p_client_id AND a.professional_id = p_professional_id AND a.status = 'completed'
    AND NOT EXISTS (SELECT 1 FROM reviews r WHERE r.appointment_id = a.id)
  ORDER BY a.starts_at DESC;
END$$

DELIMITER ;
