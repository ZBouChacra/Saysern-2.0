-- V016: premium listings. Premium is no longer a flag an admin flips: a listing is premium on the days it holds a slot from a PAID batch.

ALTER TABLE users DROP COLUMN is_premium;

-- A price and a limit per place. A country row sets the limit of premium listings per country (a batch for any city counts against its country);
-- a city row may set its own fee and/or an extra limit for that city. Anything left empty is inherited (city -> country -> Settings).
CREATE TABLE premium_pricing (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  country_code VARCHAR(100) NOT NULL,
  city        VARCHAR(100) NULL,
  daily_fee   DECIMAL(10,2) NULL,
  max_premium INT NULL,
  scope_key   VARCHAR(250) GENERATED ALWAYS AS (CONCAT(country_code, '|', LOWER(IFNULL(city, '')))) STORED,
  PRIMARY KEY (id),
  UNIQUE KEY uq_premium_pricing_scope (scope_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One purchase of premium days for one listing. The days are PAID FOR (entitled_days); the days actually held are in premium_days and
-- can shift later (see sp_premium_reallocate). The fee is fixed when the batch is created. Snapshots keep the history readable.
CREATE TABLE premium_batches (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  profession_id   BIGINT UNSIGNED NULL,
  user_id         BIGINT UNSIGNED NULL,
  professional_name VARCHAR(220) NOT NULL,
  service_name    VARCHAR(200) NOT NULL,
  country_code    VARCHAR(100) NOT NULL,
  city            VARCHAR(100) NULL,
  date_from       DATE NOT NULL,
  date_to         DATE NOT NULL,
  entitled_days   INT NOT NULL,
  days_assigned   INT NOT NULL DEFAULT 0,
  effective_to    DATE NULL,
  fee_per_day     DECIMAL(10,2) NOT NULL,
  total_fee       DECIMAL(12,2) NOT NULL,
  status          ENUM('pending','paid','cancelled') NOT NULL DEFAULT 'pending',
  paid_at         DATETIME(3) NULL,
  created_by      BIGINT UNSIGNED NULL,
  created_at      DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_premium_batches_country (country_code, status, paid_at),
  KEY ix_premium_batches_profession (profession_id, status),
  CONSTRAINT fk_premium_batches_profession FOREIGN KEY (profession_id) REFERENCES professions (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE premium_days (
  batch_id      BIGINT UNSIGNED NOT NULL,
  day           DATE NOT NULL,
  profession_id BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (batch_id, day),
  KEY ix_premium_days_profession (profession_id, day),
  KEY ix_premium_days_day (day),
  CONSTRAINT fk_premium_days_batch FOREIGN KEY (batch_id) REFERENCES premium_batches (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Premium.DefaultDailyFee',    'Premium', 'decimal', '10.00', '10.00', 0,  100000, 0, 'Daily fee (USD) for a premium listing when no price is set for the place (Premium -> Prices and limits).'),
 ('Premium.DefaultMaxPerCountry','Premium', 'int',    '10',    '10',    1,  1000,   0, 'How many listings may be premium on the same day in one country when no limit is set for it.'),
 ('Premium.HorizonDays',        'Premium', 'int',     '365',   '365',   30, 600,    0, 'How far past its original last day a batch may be pushed to make up days lost to a lowered limit. A batch that cannot be given all its days within this time cannot be marked paid.');
INSERT INTO permissions (code, group_name, description) VALUES
 ('premium.read',   'Premium', 'See premium batches, prices, limits and capacity.'),
 ('premium.manage', 'Premium', 'Create premium batches, mark them paid or cancelled, and set premium prices and limits.');
