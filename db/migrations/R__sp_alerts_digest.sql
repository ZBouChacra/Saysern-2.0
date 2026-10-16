-- Repeatable: saved-search digests.

DROP PROCEDURE IF EXISTS sp_alerts_due;
DROP PROCEDURE IF EXISTS sp_alerts_digest_get;
DROP PROCEDURE IF EXISTS sp_alerts_digest_mark;
DROP PROCEDURE IF EXISTS sp_professions_created_since;

DELIMITER $$

-- Saved searches whose interval (days, from the frequency lookup) has passed since their last digest (or since they were created). A frequency that an
-- admin switched off, or one without a length, is never due.
CREATE PROCEDURE sp_alerts_due(IN p_limit INT)
BEGIN
  DECLARE v_limit INT;
  SET v_limit = LEAST(GREATEST(IFNULL(p_limit, 100), 1), 1000);
  SELECT a.id, CAST(JSON_UNQUOTE(JSON_EXTRACT(f.metadata, '$.days')) AS SIGNED) AS days
  FROM alerts a
  JOIN users u ON u.id = a.user_id AND u.is_locked = 0
  JOIN lookup_values f ON f.type_code = 'alert_frequency' AND f.code = a.frequency AND f.is_active = 1
  WHERE JSON_EXTRACT(f.metadata, '$.days') IS NOT NULL
    AND TIMESTAMPDIFF(SECOND, COALESCE(a.last_digest_at, a.created_at), UTC_TIMESTAMP(3)) >= CAST(JSON_UNQUOTE(JSON_EXTRACT(f.metadata, '$.days')) AS SIGNED) * 86400
  ORDER BY COALESCE(a.last_digest_at, a.created_at), a.id
  LIMIT v_limit;
END$$

CREATE PROCEDURE sp_alerts_digest_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT a.id, a.user_id, a.name, a.search_criteria, COALESCE(a.last_digest_at, a.created_at) AS since, u.is_locked
  FROM alerts a JOIN users u ON u.id = a.user_id WHERE a.id = p_id;
END$$

CREATE PROCEDURE sp_alerts_digest_mark(IN p_id BIGINT UNSIGNED)
BEGIN
  UPDATE alerts SET last_digest_at = UTC_TIMESTAMP(3) WHERE id = p_id;
END$$

-- Of these profession ids (a JSON array), the ones created after p_since.
CREATE PROCEDURE sp_professions_created_since(IN p_ids JSON, IN p_since DATETIME(3))
BEGIN
  SELECT p.id FROM professions p JOIN JSON_TABLE(p_ids, '$[*]' COLUMNS (id BIGINT UNSIGNED PATH '$')) j ON j.id = p.id
  WHERE p.created_at > p_since ORDER BY p.created_at DESC, p.id DESC;
END$$

DELIMITER ;
