-- Repeatable: premium listings. Every admin change is audited in the same transaction.

DROP PROCEDURE IF EXISTS sp_premium_pricing_list;
DROP PROCEDURE IF EXISTS sp_premium_pricing_save;
DROP PROCEDURE IF EXISTS sp_premium_pricing_delete;
DROP PROCEDURE IF EXISTS sp_premium_pricing_resolve;
DROP PROCEDURE IF EXISTS sp_premium_find_professions;
DROP PROCEDURE IF EXISTS sp_premium_batches_create;
DROP PROCEDURE IF EXISTS sp_premium_batches_set_status;
DROP PROCEDURE IF EXISTS sp_premium_batches_list;
DROP PROCEDURE IF EXISTS sp_premium_capacity_days;
DROP PROCEDURE IF EXISTS sp_premium_reallocate;
DROP PROCEDURE IF EXISTS sp_premium_reallocate_all;

DELIMITER $$

-- ------------------------------------------------------------------ the allocation
-- Decides, for one country, which days every PAID batch actually holds. This is the rule the business confirmed:
--  * batches are served in the order they were PAID (first paid, first served), so a batch that was paid first always keeps its days;
--  * each day of a country has a limit of premium listings (and a city may have its own extra limit);
--  * a batch that finds a day full does not hold it; it is compensated by being pushed later, so it still receives exactly the days that were paid for,
--    skipping any day that is full, up to Premium.HorizonDays ahead;
--  * days already past are never changed (history is frozen); today and later are recomputed, so lowering a limit takes effect at once and raising it
--    lets later batches move back.
-- The caller holds the country's lock (it runs inside the caller's transaction).
CREATE PROCEDURE sp_premium_reallocate(IN p_country VARCHAR(100))
BEGIN
  DECLARE v_done INT DEFAULT 0;
  DECLARE v_batch BIGINT UNSIGNED;
  DECLARE v_prof BIGINT UNSIGNED;
  DECLARE v_city VARCHAR(100);
  DECLARE v_from DATE;
  DECLARE v_to DATE;
  DECLARE v_entitled INT;
  DECLARE v_limit INT;
  DECLARE v_city_limit INT;
  DECLARE v_remaining INT;
  DECLARE v_start DATE;
  DECLARE v_horizon INT;
  DECLARE v_lock BIGINT UNSIGNED;
  DECLARE cur CURSOR FOR
    SELECT id, profession_id, city, date_from, date_to, entitled_days FROM premium_batches
     WHERE country_code = p_country AND status = 'paid' AND profession_id IS NOT NULL ORDER BY paid_at, id;
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_done = 1;

  SELECT id INTO v_lock FROM lookup_values WHERE type_code = 'country' AND code = p_country FOR UPDATE;   -- one recalculation per country at a time
  SELECT COALESCE((SELECT max_premium FROM premium_pricing WHERE country_code = p_country AND city IS NULL),
                  (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Premium.DefaultMaxPerCountry')) INTO v_limit;
  SELECT CAST(value_text AS SIGNED) INTO v_horizon FROM app_settings WHERE setting_key = 'Premium.HorizonDays';

  DROP TEMPORARY TABLE IF EXISTS tmp_pc;
  DROP TEMPORARY TABLE IF EXISTS tmp_pt;
  DROP TEMPORARY TABLE IF EXISTS tmp_pick;
  CREATE TEMPORARY TABLE tmp_pc (day DATE NOT NULL, n INT NOT NULL, PRIMARY KEY (day));
  CREATE TEMPORARY TABLE tmp_pt (day DATE NOT NULL, city_lc VARCHAR(100) NOT NULL, n INT NOT NULL, PRIMARY KEY (day, city_lc));
  CREATE TEMPORARY TABLE tmp_pick (day DATE NOT NULL, PRIMARY KEY (day));

  DELETE pd FROM premium_days pd JOIN premium_batches b ON b.id = pd.batch_id WHERE b.country_code = p_country AND pd.day >= UTC_DATE();

  OPEN cur;
  batches: LOOP
    FETCH cur INTO v_batch, v_prof, v_city, v_from, v_to, v_entitled;
    IF v_done = 1 THEN
      LEAVE batches;
    END IF;
    SELECT v_entitled - COUNT(*) INTO v_remaining FROM premium_days WHERE batch_id = v_batch;      -- days already held in the past stay
    SET v_start = GREATEST(v_from, UTC_DATE());
    SET v_city_limit = (SELECT max_premium FROM premium_pricing WHERE country_code = p_country AND city IS NOT NULL AND LOWER(city) = LOWER(IFNULL(v_city, '')) LIMIT 1);
    DELETE FROM tmp_pick;
    IF v_remaining > 0 THEN
      INSERT INTO tmp_pick (day)
      WITH RECURSIVE d (day) AS (SELECT v_start UNION ALL SELECT DATE_ADD(day, INTERVAL 1 DAY) FROM d WHERE day < DATE_ADD(GREATEST(v_to, v_start), INTERVAL v_horizon DAY))
      SELECT d.day FROM d
        LEFT JOIN tmp_pc c ON c.day = d.day
        LEFT JOIN tmp_pt t ON t.day = d.day AND t.city_lc = LOWER(IFNULL(v_city, ''))
      WHERE IFNULL(c.n, 0) < v_limit AND (v_city_limit IS NULL OR IFNULL(t.n, 0) < v_city_limit)
        AND NOT EXISTS (SELECT 1 FROM premium_days x WHERE x.profession_id = v_prof AND x.day = d.day)   -- one listing never takes two slots on a day
      ORDER BY d.day LIMIT v_remaining;
      INSERT INTO premium_days (batch_id, day, profession_id) SELECT v_batch, day, v_prof FROM tmp_pick;
      INSERT INTO tmp_pc (day, n) SELECT day, 1 FROM tmp_pick ON DUPLICATE KEY UPDATE n = n + 1;
      INSERT INTO tmp_pt (day, city_lc, n) SELECT day, LOWER(IFNULL(v_city, '')), 1 FROM tmp_pick ON DUPLICATE KEY UPDATE n = n + 1;
    END IF;
    UPDATE premium_batches
       SET days_assigned = (SELECT COUNT(*) FROM premium_days WHERE batch_id = v_batch), effective_to = (SELECT MAX(day) FROM premium_days WHERE batch_id = v_batch)
     WHERE id = v_batch;
  END LOOP;
  CLOSE cur;
  DROP TEMPORARY TABLE IF EXISTS tmp_pc;
  DROP TEMPORARY TABLE IF EXISTS tmp_pt;
  DROP TEMPORARY TABLE IF EXISTS tmp_pick;
END$$

-- Every country that has paid batches (the daily housekeeping run: keeps days honest as dates pass and as settings change).
CREATE PROCEDURE sp_premium_reallocate_all()
BEGIN
  DECLARE v_done INT DEFAULT 0;
  DECLARE v_country VARCHAR(100);
  DECLARE cur CURSOR FOR SELECT DISTINCT country_code FROM premium_batches WHERE status = 'paid';
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_done = 1;
  OPEN cur;
  countries: LOOP
    FETCH cur INTO v_country;
    IF v_done = 1 THEN
      LEAVE countries;
    END IF;
    START TRANSACTION;
    CALL sp_premium_reallocate(v_country);
    COMMIT;
  END LOOP;
  CLOSE cur;
END$$

-- ------------------------------------------------------------------ prices and limits
CREATE PROCEDURE sp_premium_pricing_list()
BEGIN
  SELECT id, country_code, city, daily_fee, max_premium FROM premium_pricing ORDER BY country_code, city IS NOT NULL, city;
END$$

CREATE PROCEDURE sp_premium_pricing_save(IN p_actor_id BIGINT UNSIGNED, IN p_country VARCHAR(100), IN p_city VARCHAR(100), IN p_daily_fee DECIMAL(10,2), IN p_max INT)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_lock BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_country IS NULL OR NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_country AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_COUNTRY';
  END IF;
  IF (p_daily_fee IS NULL AND p_max IS NULL) OR (p_daily_fee IS NOT NULL AND p_daily_fee < 0) OR (p_max IS NOT NULL AND p_max < 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_PRICING';
  END IF;
  START TRANSACTION;
  SELECT id INTO v_lock FROM lookup_values WHERE type_code = 'country' AND code = p_country FOR UPDATE;   -- the country's lock is always taken first
  INSERT INTO premium_pricing (country_code, city, daily_fee, max_premium) VALUES (p_country, NULLIF(TRIM(p_city), ''), p_daily_fee, p_max)
  ON DUPLICATE KEY UPDATE daily_fee = VALUES(daily_fee), max_premium = VALUES(max_premium);
  SELECT id INTO v_id FROM premium_pricing WHERE scope_key = CONCAT(p_country, '|', LOWER(IFNULL(NULLIF(TRIM(p_city), ''), '')));
  CALL sp_audit_write(p_actor_id, 'premium.pricing.save', 'premium_pricing', v_id, CONCAT('Set premium price/limit for ', p_country, IFNULL(CONCAT(' / ', NULLIF(TRIM(p_city), '')), '')),
                      JSON_OBJECT('dailyFee', p_daily_fee, 'maxPremium', p_max), NULL);
  CALL sp_premium_reallocate(p_country);            -- a changed limit applies at once (later-paid batches may lose days and be pushed later)
  COMMIT;
  SELECT id, country_code, city, daily_fee, max_premium FROM premium_pricing WHERE id = v_id;
END$$

CREATE PROCEDURE sp_premium_pricing_delete(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_country VARCHAR(100);
  DECLARE v_lock BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  SELECT country_code INTO v_country FROM premium_pricing WHERE id = p_id;
  IF v_country IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PRICING_NOT_FOUND';
  END IF;
  START TRANSACTION;
  SELECT id INTO v_lock FROM lookup_values WHERE type_code = 'country' AND code = v_country FOR UPDATE;
  DELETE FROM premium_pricing WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'premium.pricing.delete', 'premium_pricing', p_id, CONCAT('Removed a premium price/limit for ', v_country), NULL, NULL);
  CALL sp_premium_reallocate(v_country);
  COMMIT;
END$$

-- The fee per day, the country limit and the city limit (empty = none) that apply to a place.
CREATE PROCEDURE sp_premium_pricing_resolve(IN p_country VARCHAR(100), IN p_city VARCHAR(100))
BEGIN
  SELECT COALESCE(c.daily_fee, k.daily_fee, (SELECT CAST(value_text AS DECIMAL(10,2)) FROM app_settings WHERE setting_key = 'Premium.DefaultDailyFee')) AS daily_fee,
         COALESCE(k.max_premium, (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Premium.DefaultMaxPerCountry')) AS max_per_country,
         c.max_premium AS max_per_city
  FROM (SELECT 1 AS one) x
  LEFT JOIN premium_pricing c ON p_city IS NOT NULL AND c.country_code = p_country AND LOWER(c.city) = LOWER(p_city)
  LEFT JOIN premium_pricing k ON k.country_code = p_country AND k.city IS NULL;
END$$

-- ------------------------------------------------------------------ finding a listing and booking days
CREATE PROCEDURE sp_premium_find_professions(IN p_search VARCHAR(100), IN p_limit INT)
BEGIN
  DECLARE v_like VARCHAR(300);
  DECLARE v_limit INT;
  SET v_limit = LEAST(GREATEST(IFNULL(p_limit, 20), 1), 50);
  SET v_like = CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(IFNULL(p_search, '')), '|', '||'), '%', '|%'), '_', '|_'), '%');
  SELECT profession_id, user_id, first_name, last_name, service_name, country, city, EXISTS (SELECT 1 FROM premium_days pdx WHERE pdx.profession_id = profession_id AND pdx.day = UTC_DATE()) AS is_premium
  FROM v_professional_listing
  WHERE user_locked = 0 AND (CONCAT(first_name, ' ', last_name) LIKE v_like ESCAPE '|' OR service_name LIKE v_like ESCAPE '|' OR city LIKE v_like ESCAPE '|')
  ORDER BY last_name, first_name, profession_id LIMIT v_limit;
END$$

-- Books premium days (a calendar range) for a listing. Pending until paid. The fee per day is fixed now; premium needs the listing's country.
CREATE PROCEDURE sp_premium_batches_create(IN p_actor_id BIGINT UNSIGNED, IN p_profession_id BIGINT UNSIGNED, IN p_from DATE, IN p_to DATE)
BEGIN
  DECLARE v_user BIGINT UNSIGNED;
  DECLARE v_first VARCHAR(100);
  DECLARE v_last VARCHAR(100);
  DECLARE v_service VARCHAR(200);
  DECLARE v_country VARCHAR(100);
  DECLARE v_city VARCHAR(100);
  DECLARE v_fee DECIMAL(10,2);
  DECLARE v_days INT;
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_from IS NULL OR p_to IS NULL OR p_to < p_from OR DATEDIFF(p_to, p_from) > 365 OR p_from < UTC_DATE() THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DATES';
  END IF;
  SELECT user_id, first_name, last_name, service_name, country, city INTO v_user, v_first, v_last, v_service, v_country, v_city
    FROM v_professional_listing WHERE profession_id = p_profession_id AND user_locked = 0;
  IF v_user IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PROFESSION_NOT_FOUND';
  END IF;
  IF v_country IS NULL OR NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = v_country) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PREMIUM_NEEDS_COUNTRY';
  END IF;
  START TRANSACTION;
  IF EXISTS (SELECT 1 FROM premium_batches WHERE profession_id = p_profession_id AND status IN ('pending', 'paid') AND p_from <= COALESCE(effective_to, date_to) AND p_to >= date_from) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PREMIUM_OVERLAP';
  END IF;
  SELECT COALESCE(c.daily_fee, k.daily_fee, (SELECT CAST(value_text AS DECIMAL(10,2)) FROM app_settings WHERE setting_key = 'Premium.DefaultDailyFee')) INTO v_fee
  FROM (SELECT 1 AS one) x
  LEFT JOIN premium_pricing c ON v_city IS NOT NULL AND c.country_code = v_country AND LOWER(c.city) = LOWER(v_city)
  LEFT JOIN premium_pricing k ON k.country_code = v_country AND k.city IS NULL;
  SET v_days = DATEDIFF(p_to, p_from) + 1;
  INSERT INTO premium_batches (profession_id, user_id, professional_name, service_name, country_code, city, date_from, date_to, entitled_days, fee_per_day, total_fee, created_by)
  VALUES (p_profession_id, v_user, TRIM(CONCAT(v_first, ' ', v_last)), v_service, v_country, v_city, p_from, p_to, v_days, v_fee, v_fee * v_days, p_actor_id);
  SET v_id = LAST_INSERT_ID();
  CALL sp_audit_write(p_actor_id, 'premium.batch.create', 'premium_batch', v_id, CONCAT('Reserved premium for ', TRIM(CONCAT(v_first, ' ', v_last)), ' (', v_service, ') from ', p_from, ' to ', p_to),
                      JSON_OBJECT('professionId', p_profession_id, 'days', v_days, 'feePerDay', v_fee, 'total', v_fee * v_days), NULL);
  COMMIT;
  CALL sp_premium_batches_list(NULL, v_id, NULL);
END$$

-- pending -> paid, or pending/paid -> cancelled. Paying recalculates the country at once; if the batch cannot be given all the days it paid for
-- within the horizon the payment is refused (PREMIUM_NO_CAPACITY). Cancelling frees its days for everyone behind it.
CREATE PROCEDURE sp_premium_batches_set_status(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_status VARCHAR(10))
BEGIN
  DECLARE v_current VARCHAR(10);
  DECLARE v_country VARCHAR(100);
  DECLARE v_entitled INT;
  DECLARE v_assigned INT;
  DECLARE v_effective DATE;
  DECLARE v_lock BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_status NOT IN ('paid', 'cancelled') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_STATUS';
  END IF;
  SELECT country_code INTO v_country FROM premium_batches WHERE id = p_id;              -- (plain read: we need the country to lock it FIRST)
  START TRANSACTION;
  IF v_country IS NOT NULL THEN
    SELECT id INTO v_lock FROM lookup_values WHERE type_code = 'country' AND code = v_country FOR UPDATE;
  END IF;
  SELECT status, entitled_days INTO v_current, v_entitled FROM premium_batches WHERE id = p_id FOR UPDATE;
  IF v_current IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BATCH_NOT_FOUND';
  END IF;
  IF NOT ((v_current = 'pending' AND p_status IN ('paid', 'cancelled')) OR (v_current = 'paid' AND p_status = 'cancelled')) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_TRANSITION';
  END IF;
  UPDATE premium_batches SET status = p_status, paid_at = IF(p_status = 'paid', UTC_TIMESTAMP(3), paid_at) WHERE id = p_id;
  CALL sp_premium_reallocate(v_country);
  IF p_status = 'paid' THEN
    SELECT days_assigned, effective_to INTO v_assigned, v_effective FROM premium_batches WHERE id = p_id;
    IF v_assigned < v_entitled THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PREMIUM_NO_CAPACITY';
    END IF;
  END IF;
  CALL sp_audit_write(p_actor_id, CONCAT('premium.batch.', p_status), 'premium_batch', p_id, CONCAT('Marked premium batch #', p_id, ' ', p_status),
                      JSON_OBJECT('from', v_current, 'to', p_status, 'effectiveTo', v_effective), NULL);
  COMMIT;
  CALL sp_premium_batches_list(NULL, p_id, NULL);
END$$

CREATE PROCEDURE sp_premium_batches_list(IN p_status VARCHAR(10), IN p_id BIGINT UNSIGNED, IN p_country VARCHAR(100))
BEGIN
  SELECT b.id, b.profession_id, b.user_id, b.professional_name, b.service_name, b.country_code, b.city, b.date_from, b.date_to, b.entitled_days, b.days_assigned,
         b.effective_to, b.fee_per_day, b.total_fee, b.status, b.paid_at, b.created_at,
         EXISTS (SELECT 1 FROM premium_days d WHERE d.batch_id = b.id AND d.day = UTC_DATE()) AS active_today
  FROM premium_batches b
  WHERE (p_status IS NULL OR b.status = p_status) AND (p_id IS NULL OR b.id = p_id) AND (p_country IS NULL OR b.country_code = p_country)
  ORDER BY b.id DESC LIMIT 500;
END$$

-- Per day: how many premium slots are in use in a country (and in a city), and the limits. For the admin's preview.
CREATE PROCEDURE sp_premium_capacity_days(IN p_country VARCHAR(100), IN p_city VARCHAR(100), IN p_from DATE, IN p_to DATE)
BEGIN
  DECLARE v_limit INT;
  DECLARE v_city_limit INT;
  IF p_from IS NULL OR p_to IS NULL OR p_to < p_from OR DATEDIFF(p_to, p_from) > 365 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DATES';
  END IF;
  SELECT COALESCE((SELECT max_premium FROM premium_pricing WHERE country_code = p_country AND city IS NULL),
                  (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Premium.DefaultMaxPerCountry')) INTO v_limit;
  SET v_city_limit = (SELECT max_premium FROM premium_pricing WHERE country_code = p_country AND city IS NOT NULL AND p_city IS NOT NULL AND LOWER(city) = LOWER(p_city) LIMIT 1);
  WITH RECURSIVE d (day) AS (SELECT p_from UNION ALL SELECT DATE_ADD(day, INTERVAL 1 DAY) FROM d WHERE day < p_to)
  SELECT d.day,
         (SELECT COUNT(*) FROM premium_days x JOIN premium_batches b ON b.id = x.batch_id WHERE b.country_code = p_country AND x.day = d.day) AS used,
         v_limit AS max_premium,
         IF(p_city IS NULL, NULL, (SELECT COUNT(*) FROM premium_days x JOIN premium_batches b ON b.id = x.batch_id WHERE b.country_code = p_country AND LOWER(IFNULL(b.city, '')) = LOWER(p_city) AND x.day = d.day)) AS city_used,
         v_city_limit AS max_city
  FROM d ORDER BY d.day;
END$$

DELIMITER ;
