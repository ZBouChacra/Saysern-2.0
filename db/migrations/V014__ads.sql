-- V014: advertisements (admin-managed), calendar-date batches with a daily fee, capacity limits, and impression/click tracking.

-- Price and capacity per position and place. A row may set either value; anything it leaves empty is inherited
-- (city row -> country row -> global row for the position -> the defaults in Settings).
CREATE TABLE ad_pricing (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  position     VARCHAR(30)  NOT NULL,
  country_code VARCHAR(100) NULL,
  city         VARCHAR(100) NULL,
  daily_fee    DECIMAL(10,2) NULL,
  max_ads      INT NULL,
  scope_key    VARCHAR(250) GENERATED ALWAYS AS (CONCAT(position, '|', IFNULL(country_code, ''), '|', LOWER(IFNULL(city, '')))) STORED,
  PRIMARY KEY (id),
  UNIQUE KEY uq_ad_pricing_scope (scope_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE ads (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  title         VARCHAR(150) NOT NULL,
  position      VARCHAR(30)  NOT NULL,
  image_file_id CHAR(32)     NOT NULL,
  link_url      VARCHAR(500) NOT NULL,
  country_code  VARCHAR(100) NULL,
  city          VARCHAR(100) NULL,
  is_active     TINYINT(1)   NOT NULL DEFAULT 1,
  fee_override  DECIMAL(10,2) NULL,
  created_by    BIGINT UNSIGNED NULL,
  created_at    DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_ads_position (position, is_active),
  CONSTRAINT fk_ads_file FOREIGN KEY (image_file_id) REFERENCES files (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One run of an ad between two calendar dates (inclusive). The fee is fixed when the batch is created, so a later price change never alters it.
CREATE TABLE ad_batches (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  ad_id       BIGINT UNSIGNED NOT NULL,
  date_from   DATE NOT NULL,
  date_to     DATE NOT NULL,
  fee_per_day DECIMAL(10,2) NOT NULL,
  total_fee   DECIMAL(12,2) NOT NULL,
  status      ENUM('pending','paid','cancelled') NOT NULL DEFAULT 'pending',
  created_by  BIGINT UNSIGNED NULL,
  created_at  DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  paid_at     DATETIME(3) NULL,
  PRIMARY KEY (id),
  KEY ix_ad_batches_ad (ad_id, status),
  KEY ix_ad_batches_dates (status, date_from, date_to),
  CONSTRAINT fk_ad_batches_ad FOREIGN KEY (ad_id) REFERENCES ads (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Daily totals are kept for good; raw events (which carry a hashed session id, never an IP address) are purged after Ads.EventRetentionDays.
CREATE TABLE ad_stats_daily (
  ad_id              BIGINT UNSIGNED NOT NULL,
  day                DATE NOT NULL,
  impressions        INT UNSIGNED NOT NULL DEFAULT 0,
  unique_impressions INT UNSIGNED NOT NULL DEFAULT 0,
  clicks             INT UNSIGNED NOT NULL DEFAULT 0,
  unique_clicks      INT UNSIGNED NOT NULL DEFAULT 0,
  PRIMARY KEY (ad_id, day)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE ad_unique_seen (
  ad_id        BIGINT UNSIGNED NOT NULL,
  day          DATE NOT NULL,
  kind         ENUM('impression','click') NOT NULL,
  session_hash CHAR(16) NOT NULL,
  PRIMARY KEY (ad_id, day, kind, session_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE ad_events (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  ad_id        BIGINT UNSIGNED NOT NULL,
  kind         ENUM('impression','click') NOT NULL,
  session_hash CHAR(16) NOT NULL,
  occurred_at  DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_ad_events_time (occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Ads.RotationSeconds',    'Advertising', 'int',     '5',    '5',    2,  60,     1, 'How long each ad stays on screen before the next one in the same slot.'),
 ('Ads.MaxPerSlot',         'Advertising', 'int',     '6',    '6',    1,  20,     1, 'The most ads a visitor is offered for one slot at a time.'),
 ('Ads.DefaultDailyFee',    'Advertising', 'decimal', '5.00', '5.00', 0,  100000, 0, 'Daily fee (USD) when no price is set for the position and place (Advertising -> Prices).'),
 ('Ads.DefaultMaxAds',      'Advertising', 'int',     '5',    '5',    1,  100,    0, 'How many ads may run on the same day in one position and place when no limit is set there.'),
 ('Ads.EventRetentionDays', 'Advertising', 'int',     '90',   '90',   7,  730,    0, 'Raw impression/click records are deleted after this many days (daily totals are kept).'),
 ('Ads.EventsPerMinute',    'Rate limits', 'int',     '120',  '120',  10, 5000,   0, 'Impression and click reports accepted per visitor address per minute.');
INSERT INTO permissions (code, group_name, description) VALUES
 ('ads.read',   'Advertising', 'See ads, prices, batches and statistics.'),
 ('ads.manage', 'Advertising', 'Create and change ads, prices and batches (including marking a batch paid).');

INSERT INTO lookup_types (code, name, description, is_system) VALUES ('ad_position', 'Ad positions', 'Places on the site where ads appear.', 1);
CREATE TEMPORARY TABLE tmp_seed (code VARCHAR(100), sort_order INT, metadata JSON, name_en VARCHAR(200), name_ar VARCHAR(200));
INSERT INTO tmp_seed VALUES
 ('home_banner',   1, '{"size":"1200x300"}', 'Home page banner', 'لافتة الصفحة الرئيسية'),
 ('search_banner', 2, '{"size":"900x200"}',  'Search results banner', 'لافتة نتائج البحث'),
 ('sidebar',       3, '{"size":"300x600"}',  'Sidebar', 'الشريط الجانبي');
INSERT INTO lookup_values (type_code, code, sort_order, metadata) SELECT 'ad_position', code, sort_order, metadata FROM tmp_seed;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'en', s.name_en FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'ad_position' AND v.code = s.code;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'ar', s.name_ar FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'ad_position' AND v.code = s.code;
DROP TEMPORARY TABLE tmp_seed;
