-- Repeatable: profiles, professions (service listings), availability and search.
-- Ownership is enforced here: every write takes the acting user's id and only touches that user's rows.

DROP PROCEDURE IF EXISTS sp_profile_get_private;
DROP PROCEDURE IF EXISTS sp_profile_get_public;
DROP PROCEDURE IF EXISTS sp_profile_update;
DROP PROCEDURE IF EXISTS sp_professions_get;
DROP PROCEDURE IF EXISTS sp_professions_create;
DROP PROCEDURE IF EXISTS sp_professions_update;
DROP PROCEDURE IF EXISTS sp_professions_delete;
DROP PROCEDURE IF EXISTS sp_professions_list_by_user;
DROP PROCEDURE IF EXISTS sp_professions_list_public;
DROP PROCEDURE IF EXISTS sp_availability_set;
DROP PROCEDURE IF EXISTS sp_availability_list_by_user;
DROP PROCEDURE IF EXISTS sp_professionals_search;
DROP PROCEDURE IF EXISTS sp_professionals_top;

DELIMITER $$

-- Everything the owner may see about themselves (includes email, phone, date of birth).
CREATE PROCEDURE sp_profile_get_private(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT id, email, email_verified, first_name, last_name, phone, sex, date_of_birth, nationality, country, city,
         profile_photo, banner_photo, bio, portfolio, preferred_language, profile_type, role, EXISTS (SELECT 1 FROM premium_days pdx JOIN professions prx ON prx.id = pdx.profession_id WHERE prx.user_id = users.id AND pdx.day = UTC_DATE()) AS is_premium, is_starred
  FROM users WHERE id = p_user_id;
END$$

-- What the public sees: no email, phone or date of birth (age only). Only listed professionals are visible.
CREATE PROCEDURE sp_profile_get_public(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT id, first_name, last_name, sex, nationality, country, city, profile_photo, banner_photo, bio, portfolio, time_zone,
         TIMESTAMPDIFF(YEAR, date_of_birth, UTC_DATE()) AS age, EXISTS (SELECT 1 FROM premium_days pdx JOIN professions prx ON prx.id = pdx.profession_id WHERE prx.user_id = users.id AND pdx.day = UTC_DATE()) AS is_premium, is_starred
  FROM users
  WHERE id = p_user_id AND profile_type = 'professional' AND is_locked = 0 AND is_disabled = 0;
END$$

-- Replaces the editable profile fields (NULL clears an optional field).
CREATE PROCEDURE sp_profile_update(
  IN p_user_id BIGINT UNSIGNED, IN p_first_name VARCHAR(100), IN p_last_name VARCHAR(100), IN p_phone VARCHAR(50),
  IN p_sex VARCHAR(10), IN p_date_of_birth DATE, IN p_nationality VARCHAR(100), IN p_country VARCHAR(100),
  IN p_city VARCHAR(100), IN p_bio TEXT, IN p_portfolio TEXT, IN p_profile_photo VARCHAR(500),
  IN p_banner_photo VARCHAR(500), IN p_preferred_language VARCHAR(10), IN p_profile_type VARCHAR(20))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_current VARCHAR(20);
  DECLARE v_professions INT;

  SELECT id, profile_type INTO v_id, v_current FROM users WHERE id = p_user_id;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  -- values must exist (and be active) in the lookups an admin maintains
  IF p_sex IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'sex' AND code = p_sex AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_SEX';
  END IF;
  IF p_nationality IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'nationality' AND code = p_nationality AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_NATIONALITY';
  END IF;
  IF p_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_country AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_COUNTRY';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'language' AND code = p_preferred_language AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_LANGUAGE';
  END IF;
  -- a photo may be an uploaded file ("/api/files/<id>"): it must be this user's own, finished file of the right purpose
  IF p_profile_photo LIKE '/api/files/%' AND NOT EXISTS (
       SELECT 1 FROM files WHERE id = SUBSTRING(p_profile_photo, 12) AND owner_id = p_user_id AND purpose = 'profile_photo' AND status = 'ready' AND is_deleted = 0) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FILE_NOT_FOUND';
  END IF;
  IF p_banner_photo LIKE '/api/files/%' AND NOT EXISTS (
       SELECT 1 FROM files WHERE id = SUBSTRING(p_banner_photo, 12) AND owner_id = p_user_id AND purpose = 'banner_photo' AND status = 'ready' AND is_deleted = 0) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'FILE_NOT_FOUND';
  END IF;
  -- switching back to customer would orphan live listings; hide them first
  IF v_current = 'professional' AND p_profile_type = 'customer' THEN
    SELECT COUNT(*) INTO v_professions FROM professions WHERE user_id = p_user_id AND is_hidden = 0;
    IF v_professions > 0 THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'HIDE_PROFESSIONS_FIRST';
    END IF;
  END IF;

  UPDATE users
     SET first_name = p_first_name, last_name = p_last_name, phone = p_phone, sex = p_sex,
         date_of_birth = p_date_of_birth, nationality = p_nationality, country = p_country, city = p_city,
         bio = p_bio, portfolio = p_portfolio, profile_photo = p_profile_photo, banner_photo = p_banner_photo,
         preferred_language = p_preferred_language, profile_type = p_profile_type
   WHERE id = p_user_id;
  CALL sp_profile_get_private(p_user_id);
END$$

CREATE PROCEDURE sp_professions_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT profession_id AS id, user_id, category_id, service_id, category_name, category_name_ar, service_name, service_name_ar,
         country, city, cost_per_hour, years_of_experience, website, has_team, team_size, has_office, office_address,
         office_city, office_country, geographic_areas, avg_rating, total_reviews, is_hidden, is_locked
  FROM v_professional_listing WHERE profession_id = p_id;
END$$

CREATE PROCEDURE sp_professions_create(
  IN p_user_id BIGINT UNSIGNED, IN p_category_id BIGINT UNSIGNED, IN p_service_id BIGINT UNSIGNED,
  IN p_country VARCHAR(100), IN p_city VARCHAR(100), IN p_cost_per_hour DECIMAL(10,2), IN p_years INT,
  IN p_website VARCHAR(255), IN p_has_team TINYINT(1), IN p_team_size INT, IN p_has_office TINYINT(1),
  IN p_office_address TEXT, IN p_office_city VARCHAR(100), IN p_office_country VARCHAR(100), IN p_geographic_areas JSON)
BEGIN
  DECLARE v_type VARCHAR(20);
  DECLARE v_service BIGINT UNSIGNED;
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 1062
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DUPLICATE_PROFESSION';

  IF (p_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_country AND is_active = 1))
     OR (p_office_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_office_country AND is_active = 1)) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_COUNTRY';
  END IF;
  SELECT profile_type INTO v_type FROM users WHERE id = p_user_id AND is_locked = 0;
  IF v_type IS NULL OR v_type <> 'professional' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_A_PROFESSIONAL';
  END IF;
  SELECT s.id INTO v_service FROM services s JOIN categories c ON c.id = s.category_id
   WHERE s.id = p_service_id AND s.category_id = p_category_id AND s.is_blocked = 0 AND c.is_blocked = 0;
  IF v_service IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_SERVICE';
  END IF;

  INSERT INTO professions (user_id, category_id, service_id, country, city, cost_per_hour, years_of_experience, website,
                           has_team, team_size, has_office, office_address, office_city, office_country, geographic_areas)
  VALUES (p_user_id, p_category_id, p_service_id, p_country, p_city, p_cost_per_hour, p_years, p_website,
          p_has_team, p_team_size, p_has_office, p_office_address, p_office_city, p_office_country, p_geographic_areas);
  SET v_id = LAST_INSERT_ID();
  CALL sp_professions_get(v_id);
END$$

-- Replaces the editable fields of one of the caller's own professions.
CREATE PROCEDURE sp_professions_update(
  IN p_user_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_country VARCHAR(100), IN p_city VARCHAR(100),
  IN p_cost_per_hour DECIMAL(10,2), IN p_years INT, IN p_website VARCHAR(255), IN p_has_team TINYINT(1),
  IN p_team_size INT, IN p_has_office TINYINT(1), IN p_office_address TEXT, IN p_office_city VARCHAR(100),
  IN p_office_country VARCHAR(100), IN p_geographic_areas JSON, IN p_is_hidden TINYINT(1))
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM professions WHERE id = p_id AND user_id = p_user_id;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PROFESSION_NOT_FOUND';
  END IF;
  IF (p_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_country AND is_active = 1))
     OR (p_office_country IS NOT NULL AND NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'country' AND code = p_office_country AND is_active = 1)) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_COUNTRY';
  END IF;
  UPDATE professions
     SET country = p_country, city = p_city, cost_per_hour = p_cost_per_hour, years_of_experience = p_years,
         website = p_website, has_team = p_has_team, team_size = p_team_size, has_office = p_has_office,
         office_address = p_office_address, office_city = p_office_city, office_country = p_office_country,
         geographic_areas = p_geographic_areas, is_hidden = p_is_hidden
   WHERE id = p_id;
  CALL sp_professions_get(p_id);
END$$

CREATE PROCEDURE sp_professions_delete(IN p_user_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  SELECT id INTO v_id FROM professions WHERE id = p_id AND user_id = p_user_id;
  IF v_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PROFESSION_NOT_FOUND';
  END IF;
  DELETE FROM professions WHERE id = p_id;   -- availability cascades; past appointments keep their data
END$$

CREATE PROCEDURE sp_professions_list_by_user(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT profession_id AS id, user_id, category_id, service_id, category_name, category_name_ar, service_name, service_name_ar,
         country, city, cost_per_hour, years_of_experience, website, has_team, team_size, has_office, office_address,
         office_city, office_country, geographic_areas, avg_rating, total_reviews, is_hidden, is_locked
  FROM v_professional_listing WHERE user_id = p_user_id ORDER BY profession_id;
END$$

CREATE PROCEDURE sp_professions_list_public(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT profession_id AS id, user_id, category_id, service_id, category_name, category_name_ar, service_name, service_name_ar,
         country, city, cost_per_hour, years_of_experience, website, has_team, team_size, has_office, office_address,
         office_city, office_country, geographic_areas, avg_rating, total_reviews, is_hidden, is_locked
  FROM v_professional_listing
  WHERE user_id = p_user_id AND is_hidden = 0 AND is_locked = 0 AND user_locked = 0 AND profile_type = 'professional'
  ORDER BY profession_id;
END$$

-- Replaces one availability set (p_profession_id NULL = the general set). p_slots is a JSON array of
-- {"dayOfWeek":0-6,"startTime":"HH:mm","endTime":"HH:mm"}; the table's CHECK constraints reject bad rows.
CREATE PROCEDURE sp_availability_set(IN p_user_id BIGINT UNSIGNED, IN p_profession_id BIGINT UNSIGNED, IN p_slots JSON)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE EXIT HANDLER FOR 3819
  BEGIN
    ROLLBACK;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_AVAILABILITY';
  END;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  IF p_profession_id IS NOT NULL THEN
    SELECT id INTO v_id FROM professions WHERE id = p_profession_id AND user_id = p_user_id;
    IF v_id IS NULL THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PROFESSION_NOT_FOUND';
    END IF;
  END IF;

  START TRANSACTION;
  DELETE FROM availability
   WHERE user_id = p_user_id
     AND ((p_profession_id IS NULL AND profession_id IS NULL) OR profession_id = p_profession_id);
  INSERT INTO availability (user_id, profession_id, day_of_week, start_time, end_time)
  SELECT p_user_id, p_profession_id, j.day_of_week, j.start_time, j.end_time
  FROM JSON_TABLE(p_slots, '$[*]' COLUMNS (
         day_of_week TINYINT UNSIGNED PATH '$.dayOfWeek',
         start_time  TIME PATH '$.startTime',
         end_time    TIME PATH '$.endTime')) AS j;
  COMMIT;
END$$

CREATE PROCEDURE sp_availability_list_by_user(IN p_user_id BIGINT UNSIGNED)
BEGIN
  SELECT id, profession_id, day_of_week, TIME_FORMAT(start_time, '%H:%i') AS start_time, TIME_FORMAT(end_time, '%H:%i') AS end_time
  FROM availability WHERE user_id = p_user_id ORDER BY day_of_week, start_time;
END$$

-- Search. NULL parameter = filter off. Two result sets: the page of listings, then the total match count.
-- Ranking: premium, starred, rating, experience. User text is LIKE-escaped (| is the escape character).
CREATE PROCEDURE sp_professionals_search(
  IN p_category_id BIGINT UNSIGNED, IN p_service_id BIGINT UNSIGNED, IN p_first_name VARCHAR(100), IN p_last_name VARCHAR(100),
  IN p_sex VARCHAR(10), IN p_min_age INT, IN p_max_age INT, IN p_nationality VARCHAR(100), IN p_country VARCHAR(100),
  IN p_city VARCHAR(100), IN p_has_office TINYINT(1), IN p_min_stars DECIMAL(3,2), IN p_min_cost DECIMAL(10,2),
  IN p_max_cost DECIMAL(10,2), IN p_min_experience INT, IN p_max_experience INT, IN p_has_team TINYINT(1),
  IN p_page INT, IN p_page_size INT, IN p_visitor_country VARCHAR(100), IN p_visitor_city VARCHAR(100))
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_first VARCHAR(220);
  DECLARE v_last VARCHAR(220);
  DECLARE v_city VARCHAR(220);

  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Search.DefaultPageSize')), 1), (SELECT CAST(value_text AS SIGNED) FROM app_settings WHERE setting_key = 'Search.MaxPageSize'));
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_first = IF(p_first_name IS NULL OR CHAR_LENGTH(TRIM(p_first_name)) = 0, NULL,
                   CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_first_name), '|', '||'), '%', '|%'), '_', '|_'), '%'));
  SET v_last = IF(p_last_name IS NULL OR CHAR_LENGTH(TRIM(p_last_name)) = 0, NULL,
                  CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_last_name), '|', '||'), '%', '|%'), '_', '|_'), '%'));
  SET v_city = IF(p_city IS NULL OR CHAR_LENGTH(TRIM(p_city)) = 0, NULL,
                  CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_city), '|', '||'), '%', '|%'), '_', '|_'), '%'));

  SELECT l.user_id, l.profession_id, l.category_id, l.service_id, l.category_name, l.category_name_ar,
         l.service_name, l.service_name_ar, l.cost_per_hour, l.years_of_experience, l.avg_rating, l.total_reviews,
         l.has_team, l.team_size, l.geographic_areas, l.first_name, l.last_name, l.profile_photo, l.sex, l.nationality,
         l.age, l.is_premium, l.is_starred, l.country, l.city, l.has_office, l.office_city, l.office_country
  FROM v_professional_listing l
  WHERE l.is_hidden = 0 AND l.is_locked = 0 AND l.user_locked = 0 AND l.profile_type = 'professional'
    AND (p_category_id IS NULL OR l.category_id = p_category_id)
    AND (p_service_id IS NULL OR l.service_id = p_service_id)
    AND (v_first IS NULL OR l.first_name LIKE v_first ESCAPE '|')
    AND (v_last IS NULL OR l.last_name LIKE v_last ESCAPE '|')
    AND (p_sex IS NULL OR l.sex = p_sex)
    AND (p_min_age IS NULL OR l.age >= p_min_age)
    AND (p_max_age IS NULL OR l.age <= p_max_age)
    AND (p_nationality IS NULL OR l.nationality = p_nationality)
    AND (p_country IS NULL OR l.country = p_country)
    AND (v_city IS NULL OR l.city LIKE v_city ESCAPE '|')
    AND (IFNULL(p_has_office, 0) = 0 OR l.has_office = 1)
    AND (p_min_stars IS NULL OR l.avg_rating >= p_min_stars)
    AND (p_min_cost IS NULL OR l.cost_per_hour >= p_min_cost)
    AND (p_max_cost IS NULL OR l.cost_per_hour <= p_max_cost)
    AND (p_min_experience IS NULL OR l.years_of_experience >= p_min_experience)
    AND (p_max_experience IS NULL OR l.years_of_experience <= p_max_experience)
    AND (p_has_team IS NULL OR l.has_team = p_has_team)
  -- Ranking: premium first, then featured, then everyone else. Inside each group the visitor's own city comes first, then the rest of their
  -- country, then elsewhere; after that rating and experience. (With no known visitor place everyone counts as "elsewhere" and rating decides.)
  ORDER BY l.is_premium DESC, l.is_starred DESC,
           CASE WHEN p_visitor_country IS NULL OR l.country IS NULL OR l.country <> p_visitor_country THEN 2
                WHEN p_visitor_city IS NOT NULL AND l.city IS NOT NULL AND LOWER(l.city) = LOWER(p_visitor_city) THEN 0
                ELSE 1 END ASC,
           l.avg_rating DESC, l.years_of_experience DESC, l.profession_id ASC
  LIMIT v_size OFFSET v_offset;

  SELECT COUNT(*) AS total
  FROM v_professional_listing l
  WHERE l.is_hidden = 0 AND l.is_locked = 0 AND l.user_locked = 0 AND l.profile_type = 'professional'
    AND (p_category_id IS NULL OR l.category_id = p_category_id)
    AND (p_service_id IS NULL OR l.service_id = p_service_id)
    AND (v_first IS NULL OR l.first_name LIKE v_first ESCAPE '|')
    AND (v_last IS NULL OR l.last_name LIKE v_last ESCAPE '|')
    AND (p_sex IS NULL OR l.sex = p_sex)
    AND (p_min_age IS NULL OR l.age >= p_min_age)
    AND (p_max_age IS NULL OR l.age <= p_max_age)
    AND (p_nationality IS NULL OR l.nationality = p_nationality)
    AND (p_country IS NULL OR l.country = p_country)
    AND (v_city IS NULL OR l.city LIKE v_city ESCAPE '|')
    AND (IFNULL(p_has_office, 0) = 0 OR l.has_office = 1)
    AND (p_min_stars IS NULL OR l.avg_rating >= p_min_stars)
    AND (p_min_cost IS NULL OR l.cost_per_hour >= p_min_cost)
    AND (p_max_cost IS NULL OR l.cost_per_hour <= p_max_cost)
    AND (p_min_experience IS NULL OR l.years_of_experience >= p_min_experience)
    AND (p_max_experience IS NULL OR l.years_of_experience <= p_max_experience)
    AND (p_has_team IS NULL OR l.has_team = p_has_team);
END$$

CREATE PROCEDURE sp_professionals_top(IN p_limit INT)
BEGIN
  SELECT l.user_id, l.profession_id, l.category_id, l.service_id, l.category_name, l.category_name_ar,
         l.service_name, l.service_name_ar, l.cost_per_hour, l.years_of_experience, l.avg_rating, l.total_reviews,
         l.has_team, l.team_size, l.geographic_areas, l.first_name, l.last_name, l.profile_photo, l.sex, l.nationality,
         l.age, l.is_premium, l.is_starred, l.country, l.city, l.has_office, l.office_city, l.office_country
  FROM v_professional_listing l
  WHERE l.is_hidden = 0 AND l.is_locked = 0 AND l.user_locked = 0 AND l.profile_type = 'professional'
  ORDER BY l.is_premium DESC, l.is_starred DESC, l.avg_rating DESC, l.years_of_experience DESC, l.profession_id ASC
  LIMIT p_limit;
END$$

DELIMITER ;
