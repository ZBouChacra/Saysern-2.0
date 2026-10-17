-- Repeatable: advertisements. Every admin change is audited in the same transaction.

DROP PROCEDURE IF EXISTS sp_ad_pricing_list;
DROP PROCEDURE IF EXISTS sp_ad_pricing_save;
DROP PROCEDURE IF EXISTS sp_ad_pricing_delete;
DROP PROCEDURE IF EXISTS sp_ad_pricing_resolve;
DROP PROCEDURE IF EXISTS sp_ads_save;
DROP PROCEDURE IF EXISTS sp_ads_list;
DROP PROCEDURE IF EXISTS sp_ads_get;
DROP PROCEDURE IF EXISTS sp_ad_batches_create;
DROP PROCEDURE IF EXISTS sp_ad_batches_set_status;
DROP PROCEDURE IF EXISTS sp_ad_batches_list;
DROP PROCEDURE IF EXISTS sp_ad_capacity_days;
DROP PROCEDURE IF EXISTS sp_ads_serve;
DROP PROCEDURE IF EXISTS sp_ad_record;
DROP PROCEDURE IF EXISTS sp_ad_stats;
DROP PROCEDURE IF EXISTS sp_ad_stats_daily;
DROP PROCEDURE IF EXISTS sp_ad_events_purge;

DELIMITER $$

-- ------------------------------------------------------------------ prices and limits
CREATE PROCEDURE sp_ad_pricing_list()
BEGIN
  SELECT id, position, country_code, city, daily_fee, max_ads FROM ad_pricing ORDER BY position, country_code IS NOT NULL, country_code, city IS NOT NULL, city;
END$$

CREATE PROCEDURE sp_ad_pricing_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_position VARCHAR(30), IN p_country VARCHAR(100), IN p_city VARCHAR(100), IN p_daily_fee DECIMAL(10,2), IN p_max_ads INT)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'ad_position' AND code = p_position AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_AD_POSITION';
  END IF;
  IF (p_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_country AND is_active = 1))
     OR (p_city IS NOT NULL AND (p_country IS NULL OR CHAR_LENGTH(TRIM(p_city)) = 0)) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_COUNTRY';
  END IF;
  IF (p_daily_fee IS NULL AND p_max_ads IS NULL) OR (p_daily_fee IS NOT NULL AND p_daily_fee < 0) OR (p_max_ads IS NOT NULL AND p_max_ads < 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_PRICING';
  END IF;
  START TRANSACTION;
  INSERT INTO ad_pricing (position, country_code, city, daily_fee, max_ads) VALUES (p_position, p_country, NULLIF(TRIM(p_city), ''), p_daily_fee, p_max_ads)
  ON DUPLICATE KEY UPDATE daily_fee = VALUES(daily_fee), max_ads = VALUES(max_ads);
  SELECT id INTO v_id FROM ad_pricing WHERE scope_key = CONCAT(p_position, '|', IFNULL(p_country, ''), '|', LOWER(IFNULL(NULLIF(TRIM(p_city), ''), '')));
  CALL sp_audit_write(p_actor_id, 'ads.pricing.save', 'ad_pricing', v_id,
                      CONCAT('Set ad price/limit for ', p_position, IFNULL(CONCAT(' / ', p_country), ' / everywhere'), IFNULL(CONCAT(' / ', p_city), '')),
                      JSON_OBJECT('dailyFee', p_daily_fee, 'maxAds', p_max_ads), NULL);
  COMMIT;
  SELECT id, position, country_code, city, daily_fee, max_ads FROM ad_pricing WHERE id = v_id;
END$$

CREATE PROCEDURE sp_ad_pricing_delete(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_pos VARCHAR(30);
  SELECT position INTO v_pos FROM ad_pricing WHERE id = p_id;
  IF v_pos IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PRICING_NOT_FOUND';
  END IF;
  START TRANSACTION;
  DELETE FROM ad_pricing WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'ads.pricing.delete', 'ad_pricing', p_id, CONCAT('Removed an ad price/limit for ', v_pos), NULL, NULL);
  COMMIT;
END$$

-- The fee and the daily limit that apply to a position in a place: city row, else country row, else the position's global row, else the defaults in Settings.
-- Each value is resolved on its own (a city row may set only the limit and inherit the fee).
CREATE PROCEDURE sp_ad_pricing_resolve(IN p_position VARCHAR(30), IN p_country VARCHAR(100), IN p_city VARCHAR(100))
BEGIN
  SELECT COALESCE(c.daily_fee, k.daily_fee, g.daily_fee, (SELECT CAST(value_text AS DECIMAL(10,2)) FROM app_settings WHERE setting_key = 'Ads.DefaultDailyFee')) AS daily_fee,
         COALESCE(c.max_ads,   k.max_ads,   g.max_ads,   (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Ads.DefaultMaxAds')) AS max_ads
  FROM (SELECT 1 AS one) x
  LEFT JOIN ad_pricing c ON c.position = p_position AND p_country IS NOT NULL AND p_city IS NOT NULL AND c.country_code = p_country AND LOWER(c.city) = LOWER(p_city)
  LEFT JOIN ad_pricing k ON k.position = p_position AND p_country IS NOT NULL AND k.country_code = p_country AND k.city IS NULL
  LEFT JOIN ad_pricing g ON g.position = p_position AND g.country_code IS NULL AND g.city IS NULL;
END$$

-- ------------------------------------------------------------------ ads
CREATE PROCEDURE sp_ads_save(
  IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_title VARCHAR(150), IN p_position VARCHAR(30), IN p_image_file_id CHAR(32), IN p_link_url VARCHAR(500),
  IN p_country VARCHAR(100), IN p_city VARCHAR(100), IN p_is_active TINYINT(1), IN p_fee_override DECIMAL(10,2))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_title IS NULL OR CHAR_LENGTH(TRIM(p_title)) = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NAMES';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'ad_position' AND code = p_position AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_AD_POSITION';
  END IF;
  IF p_link_url IS NULL OR p_link_url NOT REGEXP '^https?://[^ ]+$' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_LINK';
  END IF;
  IF (p_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_country AND is_active = 1))
     OR (p_city IS NOT NULL AND p_country IS NULL) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_COUNTRY';
  END IF;
  IF p_fee_override IS NOT NULL AND p_fee_override < 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_PRICING';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM files WHERE id = p_image_file_id AND purpose = 'ad_image' AND status = 'ready' AND is_deleted = 0) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FILE_NOT_FOUND';
  END IF;
  START TRANSACTION;
  IF p_id IS NULL THEN
    INSERT INTO ads (title, position, image_file_id, link_url, country_code, city, is_active, fee_override, created_by)
    VALUES (TRIM(p_title), p_position, p_image_file_id, p_link_url, p_country, NULLIF(TRIM(p_city), ''), IFNULL(p_is_active, 1), p_fee_override, p_actor_id);
    SET v_id = LAST_INSERT_ID();
  ELSE
    SELECT id INTO v_id FROM ads WHERE id = p_id FOR UPDATE;
    IF v_id IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'AD_NOT_FOUND';
    END IF;
    UPDATE ads SET title = TRIM(p_title), position = p_position, image_file_id = p_image_file_id, link_url = p_link_url, country_code = p_country,
           city = NULLIF(TRIM(p_city), ''), is_active = IFNULL(p_is_active, is_active), fee_override = p_fee_override WHERE id = v_id;
  END IF;
  CALL sp_audit_write(p_actor_id, IF(p_id IS NULL, 'ads.create', 'ads.update'), 'ad', v_id, CONCAT(IF(p_id IS NULL, 'Created ad "', 'Edited ad "'), TRIM(p_title), '"'),
                      JSON_OBJECT('position', p_position, 'country', p_country, 'city', p_city, 'active', IFNULL(p_is_active, 1)), NULL);
  COMMIT;
  CALL sp_ads_get(v_id);
END$$

CREATE PROCEDURE sp_ads_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT id, title, position, image_file_id, link_url, country_code, city, is_active, fee_override, created_at FROM ads WHERE id = p_id;
END$$

CREATE PROCEDURE sp_ads_list(IN p_position VARCHAR(30))
BEGIN
  SELECT a.id, a.title, a.position, a.image_file_id, a.link_url, a.country_code, a.city, a.is_active, a.fee_override, a.created_at,
         EXISTS (SELECT 1 FROM ad_batches b WHERE b.ad_id = a.id AND b.status = 'paid' AND UTC_DATE() BETWEEN b.date_from AND b.date_to) AS running_today
  FROM ads a WHERE p_position IS NULL OR a.position = p_position ORDER BY a.id DESC;
END$$

-- ------------------------------------------------------------------ batches
-- For each day of a candidate run: how many OTHER ads are already booked that overlap this ad's place, and the limit. (For previews and for the hard stop.)
CREATE PROCEDURE sp_ad_capacity_days(IN p_ad_id BIGINT UNSIGNED, IN p_from DATE, IN p_to DATE)
BEGIN
  DECLARE v_pos VARCHAR(30);
  DECLARE v_country VARCHAR(100);
  DECLARE v_city VARCHAR(100);
  DECLARE v_max INT;
  SELECT position, country_code, city INTO v_pos, v_country, v_city FROM ads WHERE id = p_ad_id;
  IF v_pos IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'AD_NOT_FOUND';
  END IF;
  IF p_to < p_from OR DATEDIFF(p_to, p_from) > 365 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DATES';
  END IF;
  SELECT COALESCE(c.max_ads, k.max_ads, g.max_ads, (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Ads.DefaultMaxAds')) INTO v_max
  FROM (SELECT 1 AS one) x
  LEFT JOIN ad_pricing c ON c.position = v_pos AND v_country IS NOT NULL AND v_city IS NOT NULL AND c.country_code = v_country AND LOWER(c.city) = LOWER(v_city)
  LEFT JOIN ad_pricing k ON k.position = v_pos AND v_country IS NOT NULL AND k.country_code = v_country AND k.city IS NULL
  LEFT JOIN ad_pricing g ON g.position = v_pos AND g.country_code IS NULL AND g.city IS NULL;
  WITH RECURSIVE d (day) AS (SELECT p_from UNION ALL SELECT DATE_ADD(day, INTERVAL 1 DAY) FROM d WHERE day < p_to)
  SELECT d.day,
         (SELECT COUNT(DISTINCT b.ad_id) FROM ad_batches b JOIN ads o ON o.id = b.ad_id
           WHERE o.position = v_pos AND o.is_active = 1 AND b.status IN ('pending', 'paid') AND b.ad_id <> p_ad_id AND d.day BETWEEN b.date_from AND b.date_to
             AND (o.country_code IS NULL OR v_country IS NULL OR (o.country_code = v_country AND (o.city IS NULL OR v_city IS NULL OR LOWER(o.city) = LOWER(v_city))))) AS booked,
         v_max AS max_ads
  FROM d ORDER BY d.day;
END$$

-- Reserves a run of an ad between two dates. The fee per day is fixed now. Refused (AD_CAPACITY_EXCEEDED) if any day would exceed the limit.
-- Serialised per position (the position's lookup row is the lock) so two admins cannot overbook at the same moment.
CREATE PROCEDURE sp_ad_batches_create(IN p_actor_id BIGINT UNSIGNED, IN p_ad_id BIGINT UNSIGNED, IN p_from DATE, IN p_to DATE)
BEGIN
  DECLARE v_pos VARCHAR(30);
  DECLARE v_country VARCHAR(100);
  DECLARE v_city VARCHAR(100);
  DECLARE v_override DECIMAL(10,2);
  DECLARE v_active TINYINT;
  DECLARE v_fee DECIMAL(10,2);
  DECLARE v_full INT;
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_days INT;
  DECLARE v_lock BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_from IS NULL OR p_to IS NULL OR p_to < p_from OR DATEDIFF(p_to, p_from) > 365 OR p_from < UTC_DATE() THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DATES';
  END IF;
  SELECT position, country_code, city, fee_override, is_active INTO v_pos, v_country, v_city, v_override, v_active FROM ads WHERE id = p_ad_id;
  IF v_pos IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'AD_NOT_FOUND';
  END IF;
  IF v_active = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'AD_INACTIVE';
  END IF;
  START TRANSACTION;
  SELECT id INTO v_lock FROM lookup_values WHERE type_code = 'ad_position' AND code = v_pos FOR UPDATE;
  SELECT COUNT(*) INTO v_full FROM (
    SELECT t.booked, t.max_ads FROM (
      WITH RECURSIVE d (day) AS (SELECT p_from UNION ALL SELECT DATE_ADD(day, INTERVAL 1 DAY) FROM d WHERE day < p_to)
      SELECT d.day,
             (SELECT COUNT(DISTINCT b.ad_id) FROM ad_batches b JOIN ads o ON o.id = b.ad_id
               WHERE o.position = v_pos AND o.is_active = 1 AND b.status IN ('pending', 'paid') AND b.ad_id <> p_ad_id AND d.day BETWEEN b.date_from AND b.date_to
                 AND (o.country_code IS NULL OR v_country IS NULL OR (o.country_code = v_country AND (o.city IS NULL OR v_city IS NULL OR LOWER(o.city) = LOWER(v_city))))) AS booked,
             (SELECT COALESCE(c.max_ads, k.max_ads, g.max_ads, (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Ads.DefaultMaxAds'))
                FROM (SELECT 1 AS one) x
                LEFT JOIN ad_pricing c ON c.position = v_pos AND v_country IS NOT NULL AND v_city IS NOT NULL AND c.country_code = v_country AND LOWER(c.city) = LOWER(v_city)
                LEFT JOIN ad_pricing k ON k.position = v_pos AND v_country IS NOT NULL AND k.country_code = v_country AND k.city IS NULL
                LEFT JOIN ad_pricing g ON g.position = v_pos AND g.country_code IS NULL AND g.city IS NULL) AS max_ads
      FROM d) t WHERE t.booked + 1 > t.max_ads) full_days;
  IF v_full > 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'AD_CAPACITY_EXCEEDED';
  END IF;
  SELECT COALESCE(v_override, c.daily_fee, k.daily_fee, g.daily_fee, (SELECT CAST(value_text AS DECIMAL(10,2)) FROM app_settings WHERE setting_key = 'Ads.DefaultDailyFee')) INTO v_fee
  FROM (SELECT 1 AS one) x
  LEFT JOIN ad_pricing c ON c.position = v_pos AND v_country IS NOT NULL AND v_city IS NOT NULL AND c.country_code = v_country AND LOWER(c.city) = LOWER(v_city)
  LEFT JOIN ad_pricing k ON k.position = v_pos AND v_country IS NOT NULL AND k.country_code = v_country AND k.city IS NULL
  LEFT JOIN ad_pricing g ON g.position = v_pos AND g.country_code IS NULL AND g.city IS NULL;
  SET v_days = DATEDIFF(p_to, p_from) + 1;
  INSERT INTO ad_batches (ad_id, date_from, date_to, fee_per_day, total_fee, created_by) VALUES (p_ad_id, p_from, p_to, v_fee, v_fee * v_days, p_actor_id);
  SET v_id = LAST_INSERT_ID();
  CALL sp_audit_write(p_actor_id, 'ads.batch.create', 'ad_batch', v_id, CONCAT('Reserved ad #', p_ad_id, ' from ', p_from, ' to ', p_to, ' (', v_days, ' days at ', v_fee, ')'),
                      JSON_OBJECT('adId', p_ad_id, 'from', p_from, 'to', p_to, 'feePerDay', v_fee, 'total', v_fee * v_days), NULL);
  COMMIT;
  SELECT id, ad_id, date_from, date_to, fee_per_day, total_fee, status, created_at, paid_at, (SELECT p.id FROM payments p WHERE p.ad_batch_id = ad_batches.id AND p.status = 'issued' LIMIT 1) AS receipt_id, (SELECT p.receipt_number FROM payments p WHERE p.ad_batch_id = ad_batches.id AND p.status = 'issued' LIMIT 1) AS receipt_number FROM ad_batches WHERE id = v_id;
END$$

-- pending -> paid, or pending/paid -> cancelled (cancelling frees the capacity). Anything else is refused.
CREATE PROCEDURE sp_ad_batches_set_status(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_status VARCHAR(10),
  IN p_billed_to VARCHAR(200), IN p_billed_details VARCHAR(500), IN p_method VARCHAR(30), IN p_reference VARCHAR(100), IN p_received_on DATE, IN p_notes VARCHAR(500))
BEGIN
  DECLARE v_current VARCHAR(10);
  DECLARE v_desc VARCHAR(400);
  DECLARE v_desc_ar VARCHAR(400);
  DECLARE v_qty INT;
  DECLARE v_unit DECIMAL(10,2);
  DECLARE v_sub DECIMAL(12,2);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_status NOT IN ('paid', 'cancelled') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_STATUS';
  END IF;
  START TRANSACTION;
  SELECT status INTO v_current FROM ad_batches WHERE id = p_id FOR UPDATE;
  IF v_current IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BATCH_NOT_FOUND';
  END IF;
  IF NOT ((v_current = 'pending' AND p_status IN ('paid', 'cancelled')) OR (v_current = 'paid' AND p_status = 'cancelled')) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TRANSITION';
  END IF;
  UPDATE ad_batches SET status = p_status, paid_at = IF(p_status = 'paid', UTC_TIMESTAMP(3), paid_at) WHERE id = p_id;
  IF p_status = 'paid' THEN        -- a batch becomes paid only together with its receipt (same transaction)
    SELECT CONCAT('Advertisement "', a.title, '" (', IFNULL((SELECT t.name FROM lookup_values v JOIN lookup_translations t ON t.value_id = v.id AND t.language = 'en' WHERE v.type_code = 'ad_position' AND v.code = a.position), a.position),
                  '), ', b.date_from, ' to ', b.date_to), DATEDIFF(b.date_to, b.date_from) + 1, b.fee_per_day, b.total_fee
      INTO v_desc, v_qty, v_unit, v_sub FROM ad_batches b JOIN ads a ON a.id = b.ad_id WHERE b.id = p_id;
    SELECT CONCAT('إعلان "', a.title, '" (', COALESCE((SELECT t.name FROM lookup_values v JOIN lookup_translations t ON t.value_id = v.id AND t.language = 'ar' WHERE v.type_code = 'ad_position' AND v.code = a.position),
                  (SELECT t.name FROM lookup_values v JOIN lookup_translations t ON t.value_id = v.id AND t.language = 'en' WHERE v.type_code = 'ad_position' AND v.code = a.position), a.position),
                  ')، ', b.date_from, ' إلى ', b.date_to)
      INTO v_desc_ar FROM ad_batches b JOIN ads a ON a.id = b.ad_id WHERE b.id = p_id;
    CALL sp_payment_issue(p_actor_id, 'ad', p_id, NULL, v_desc, v_qty, v_unit, v_sub, p_billed_to, p_billed_details, p_method, p_reference, p_received_on, p_notes);
    UPDATE payments SET description_ar = v_desc_ar WHERE ad_batch_id = p_id AND status = 'issued';
  ELSEIF v_current = 'paid' THEN
    CALL sp_payment_void(p_actor_id, p_id, NULL);
  END IF;
  CALL sp_audit_write(p_actor_id, CONCAT('ads.batch.', p_status), 'ad_batch', p_id, CONCAT('Marked ad batch #', p_id, ' ', p_status), JSON_OBJECT('from', v_current, 'to', p_status), NULL);
  COMMIT;
  SELECT id, ad_id, date_from, date_to, fee_per_day, total_fee, status, created_at, paid_at, (SELECT p.id FROM payments p WHERE p.ad_batch_id = ad_batches.id AND p.status = 'issued' LIMIT 1) AS receipt_id, (SELECT p.receipt_number FROM payments p WHERE p.ad_batch_id = ad_batches.id AND p.status = 'issued' LIMIT 1) AS receipt_number FROM ad_batches WHERE id = p_id;
END$$

CREATE PROCEDURE sp_ad_batches_list(IN p_ad_id BIGINT UNSIGNED, IN p_status VARCHAR(10))
BEGIN
  SELECT id, ad_id, date_from, date_to, fee_per_day, total_fee, status, created_at, paid_at, (SELECT p.id FROM payments p WHERE p.ad_batch_id = ad_batches.id AND p.status = 'issued' LIMIT 1) AS receipt_id, (SELECT p.receipt_number FROM payments p WHERE p.ad_batch_id = ad_batches.id AND p.status = 'issued' LIMIT 1) AS receipt_number FROM ad_batches
  WHERE (p_ad_id IS NULL OR ad_id = p_ad_id) AND (p_status IS NULL OR status = p_status) ORDER BY date_from DESC, id DESC LIMIT 500;
END$$

-- ------------------------------------------------------------------ serving and tracking
-- The ads running today (a PAID batch covers today's UTC date) for a position and a visitor's place. A global ad suits everyone, a country ad
-- suits that country, a city ad that city; with no known place only global ads are offered. More specific ads come first.
CREATE PROCEDURE sp_ads_serve(IN p_position VARCHAR(30), IN p_country VARCHAR(100), IN p_city VARCHAR(100), IN p_limit INT)
BEGIN
  DECLARE v_limit INT;
  SET v_limit = LEAST(GREATEST(IFNULL(p_limit, 6), 1), 50);
  SELECT a.id, a.title, a.position, a.image_file_id
  FROM ads a JOIN files f ON f.id = a.image_file_id AND f.status = 'ready' AND f.is_deleted = 0
  WHERE a.position = p_position AND a.is_active = 1
    AND EXISTS (SELECT 1 FROM ad_batches b WHERE b.ad_id = a.id AND b.status = 'paid' AND UTC_DATE() BETWEEN b.date_from AND b.date_to)
    AND (a.country_code IS NULL OR (a.country_code = p_country AND (a.city IS NULL OR LOWER(a.city) = LOWER(p_city))))
  ORDER BY (a.city IS NOT NULL) DESC, (a.country_code IS NOT NULL) DESC, a.id
  LIMIT v_limit;
END$$

-- Counts an impression or a click (only for an ad that is running today). "Unique" counts each hashed visitor session once per day.
-- Returns the ad's link (for the redirect on a click).
CREATE PROCEDURE sp_ad_record(IN p_ad_id BIGINT UNSIGNED, IN p_kind VARCHAR(10), IN p_session CHAR(16))
BEGIN
  DECLARE v_link VARCHAR(500);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_kind NOT IN ('impression', 'click') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'VALIDATION_FAILED';
  END IF;
  SELECT a.link_url INTO v_link FROM ads a
   WHERE a.id = p_ad_id AND a.is_active = 1 AND EXISTS (SELECT 1 FROM ad_batches b WHERE b.ad_id = a.id AND b.status = 'paid' AND UTC_DATE() BETWEEN b.date_from AND b.date_to);
  IF v_link IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'AD_NOT_FOUND';
  END IF;
  START TRANSACTION;
  INSERT INTO ad_events (ad_id, kind, session_hash) VALUES (p_ad_id, p_kind, p_session);
  INSERT INTO ad_stats_daily (ad_id, day, impressions, clicks) VALUES (p_ad_id, UTC_DATE(), IF(p_kind = 'impression', 1, 0), IF(p_kind = 'click', 1, 0))
  ON DUPLICATE KEY UPDATE impressions = impressions + VALUES(impressions), clicks = clicks + VALUES(clicks);
  INSERT IGNORE INTO ad_unique_seen (ad_id, day, kind, session_hash) VALUES (p_ad_id, UTC_DATE(), p_kind, p_session);
  IF ROW_COUNT() = 1 THEN
    UPDATE ad_stats_daily SET unique_impressions = unique_impressions + IF(p_kind = 'impression', 1, 0), unique_clicks = unique_clicks + IF(p_kind = 'click', 1, 0)
     WHERE ad_id = p_ad_id AND day = UTC_DATE();
  END IF;
  COMMIT;
  SELECT v_link AS link_url;
END$$

CREATE PROCEDURE sp_ad_stats(IN p_from DATE, IN p_to DATE)
BEGIN
  SELECT a.id AS ad_id, a.title, a.position,
         IFNULL(SUM(s.impressions), 0) AS impressions, IFNULL(SUM(s.unique_impressions), 0) AS unique_impressions,
         IFNULL(SUM(s.clicks), 0) AS clicks, IFNULL(SUM(s.unique_clicks), 0) AS unique_clicks
  FROM ads a LEFT JOIN ad_stats_daily s ON s.ad_id = a.id AND s.day BETWEEN IFNULL(p_from, '2000-01-01') AND IFNULL(p_to, '2999-12-31')
  GROUP BY a.id, a.title, a.position ORDER BY a.id DESC;
END$$

CREATE PROCEDURE sp_ad_stats_daily(IN p_ad_id BIGINT UNSIGNED, IN p_from DATE, IN p_to DATE)
BEGIN
  SELECT day, impressions, unique_impressions, clicks, unique_clicks FROM ad_stats_daily
  WHERE ad_id = p_ad_id AND day BETWEEN IFNULL(p_from, '2000-01-01') AND IFNULL(p_to, '2999-12-31') ORDER BY day;
END$$

-- Housekeeping: raw events older than the retention period go; daily totals stay. Returns how many raw events were removed.
CREATE PROCEDURE sp_ad_events_purge(IN p_days INT)
BEGIN
  DECLARE v_deleted INT;
  DELETE FROM ad_events WHERE occurred_at < DATE_SUB(UTC_TIMESTAMP(3), INTERVAL GREATEST(IFNULL(p_days, 90), 1) DAY);
  SET v_deleted = ROW_COUNT();
  DELETE FROM ad_unique_seen WHERE day < DATE_SUB(UTC_DATE(), INTERVAL 2 DAY);   -- only needed to de-duplicate within a day
  SELECT v_deleted AS deleted;
END$$

DELIMITER ;
