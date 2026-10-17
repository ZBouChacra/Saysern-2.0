-- Repeatable: the background work on appointments (expire unanswered requests, remind approved ones). Safe to run on several servers at once.

DROP PROCEDURE IF EXISTS sp_appointments_expire_pending;
DROP PROCEDURE IF EXISTS sp_appointments_reminders_due;
DROP PROCEDURE IF EXISTS sp_appointments_mark_reminded;

DELIMITER $$

-- Cancels pending requests whose start time has passed and returns what was cancelled (so the clients can be told).
-- SKIP LOCKED: a server that finds another already working on a row simply leaves it, so nothing is cancelled or announced twice.
CREATE PROCEDURE sp_appointments_expire_pending(IN p_limit INT)
BEGIN
  DECLARE v_ids JSON;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  START TRANSACTION;
  SELECT JSON_ARRAYAGG(x.id) INTO v_ids FROM (
    SELECT id FROM appointments WHERE status = 'pending' AND starts_at <= UTC_TIMESTAMP(3) ORDER BY id LIMIT 500 FOR UPDATE SKIP LOCKED) x;
  IF v_ids IS NOT NULL THEN
    UPDATE appointments SET status = 'cancelled', updated_at = UTC_TIMESTAMP(3)
     WHERE id IN (SELECT jt.id FROM JSON_TABLE(v_ids, '$[*]' COLUMNS (id BIGINT UNSIGNED PATH '$')) jt);
  END IF;
  COMMIT;
  IF v_ids IS NOT NULL THEN
    SELECT a.id, a.client_id, a.professional_id, a.starts_at, c.first_name AS client_first_name, c.last_name AS client_last_name, p.first_name AS professional_first_name, p.last_name AS professional_last_name
    FROM appointments a JOIN users c ON c.id = a.client_id JOIN users p ON p.id = a.professional_id
    WHERE a.id IN (SELECT jt.id FROM JSON_TABLE(v_ids, '$[*]' COLUMNS (id BIGINT UNSIGNED PATH '$')) jt) ORDER BY a.id LIMIT p_limit;
  END IF;
END$$

-- Approved appointments that start within the next p_hours hours and have not been reminded yet.
CREATE PROCEDURE sp_appointments_reminders_due(IN p_hours INT, IN p_limit INT)
BEGIN
  SELECT a.id, a.client_id, a.professional_id, a.starts_at, c.first_name AS client_first_name, c.last_name AS client_last_name, p.first_name AS professional_first_name, p.last_name AS professional_last_name
  FROM appointments a JOIN users c ON c.id = a.client_id JOIN users p ON p.id = a.professional_id
  WHERE a.status = 'approved' AND a.reminder_sent_at IS NULL AND a.starts_at > UTC_TIMESTAMP(3) AND a.starts_at <= DATE_ADD(UTC_TIMESTAMP(3), INTERVAL p_hours HOUR)
  ORDER BY a.starts_at, a.id LIMIT p_limit;
END$$

-- Claims the reminder: only the first caller gets marked = 1, so two servers never both send it.
CREATE PROCEDURE sp_appointments_mark_reminded(IN p_id BIGINT UNSIGNED)
BEGIN
  UPDATE appointments SET reminder_sent_at = UTC_TIMESTAMP(3) WHERE id = p_id AND reminder_sent_at IS NULL AND status = 'approved';
  SELECT ROW_COUNT() AS marked;
END$$

DELIMITER ;
