-- V003: catalog, professional listings, availability, bookings, reviews, chat, alerts.

CREATE TABLE categories (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  name           VARCHAR(100) NOT NULL,
  name_ar        VARCHAR(100) NULL,
  description    TEXT NULL,
  description_ar TEXT NULL,
  icon           VARCHAR(50) NULL,
  is_blocked     TINYINT(1) NOT NULL DEFAULT 0,
  created_at     DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_categories_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE services (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  category_id    BIGINT UNSIGNED NOT NULL,
  name           VARCHAR(100) NOT NULL,
  name_ar        VARCHAR(100) NULL,
  description    TEXT NULL,
  description_ar TEXT NULL,
  is_blocked     TINYINT(1) NOT NULL DEFAULT 0,
  created_at     DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_services_category_name (category_id, name),
  CONSTRAINT fk_services_category FOREIGN KEY (category_id) REFERENCES categories (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One row per service a professional offers. is_hidden is the owner's switch; is_locked is the admin's.
CREATE TABLE professions (
  id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id             BIGINT UNSIGNED NOT NULL,
  category_id         BIGINT UNSIGNED NOT NULL,
  service_id          BIGINT UNSIGNED NOT NULL,
  country             VARCHAR(100) NULL,
  city                VARCHAR(100) NULL,
  cost_per_hour       DECIMAL(10,2) NULL,
  years_of_experience INT UNSIGNED NOT NULL DEFAULT 0,
  website             VARCHAR(255) NULL,
  has_team            TINYINT(1) NOT NULL DEFAULT 0,
  team_size           INT UNSIGNED NOT NULL DEFAULT 0,
  has_office          TINYINT(1) NOT NULL DEFAULT 0,
  office_address      TEXT NULL,
  office_city         VARCHAR(100) NULL,
  office_country      VARCHAR(100) NULL,
  geographic_areas    JSON NULL,
  is_hidden           TINYINT(1) NOT NULL DEFAULT 0,
  is_locked           TINYINT(1) NOT NULL DEFAULT 0,
  avg_rating          DECIMAL(3,2) NOT NULL DEFAULT 0,
  total_reviews       INT UNSIGNED NOT NULL DEFAULT 0,
  created_at          DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_professions_user_service (user_id, service_id),
  KEY ix_professions_category (category_id),
  KEY ix_professions_service (service_id),
  CONSTRAINT fk_professions_user     FOREIGN KEY (user_id)     REFERENCES users (id) ON DELETE CASCADE,
  CONSTRAINT fk_professions_category FOREIGN KEY (category_id) REFERENCES categories (id),
  CONSTRAINT fk_professions_service  FOREIGN KEY (service_id)  REFERENCES services (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Weekly windows a professional says they are available (advisory: shown to clients; conflicts are prevented by the overlap check).
CREATE TABLE availability (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id       BIGINT UNSIGNED NOT NULL,
  profession_id BIGINT UNSIGNED NULL,
  day_of_week   TINYINT UNSIGNED NOT NULL,
  start_time    TIME NOT NULL,
  end_time      TIME NOT NULL,
  PRIMARY KEY (id),
  KEY ix_availability_user (user_id),
  KEY ix_availability_profession (profession_id),
  CONSTRAINT ck_availability_day  CHECK (day_of_week BETWEEN 0 AND 6),
  CONSTRAINT ck_availability_span CHECK (start_time < end_time),
  CONSTRAINT fk_availability_user       FOREIGN KEY (user_id)       REFERENCES users (id) ON DELETE CASCADE,
  CONSTRAINT fk_availability_profession FOREIGN KEY (profession_id) REFERENCES professions (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- All instants are UTC.
CREATE TABLE appointments (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  client_id        BIGINT UNSIGNED NOT NULL,
  professional_id  BIGINT UNSIGNED NOT NULL,
  profession_id    BIGINT UNSIGNED NULL,
  service_id       BIGINT UNSIGNED NULL,
  starts_at        DATETIME(3) NOT NULL,
  ends_at          DATETIME(3) NOT NULL,
  duration_minutes INT UNSIGNED NOT NULL,
  description      TEXT NULL,
  status           ENUM('pending','approved','cancelled','completed') NOT NULL DEFAULT 'pending',
  created_at       DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at       DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_appointments_professional (professional_id, starts_at),
  KEY ix_appointments_client (client_id, starts_at),
  CONSTRAINT ck_appointments_span CHECK (starts_at < ends_at),
  CONSTRAINT fk_appointments_client       FOREIGN KEY (client_id)       REFERENCES users (id),
  CONSTRAINT fk_appointments_professional FOREIGN KEY (professional_id) REFERENCES users (id),
  CONSTRAINT fk_appointments_profession   FOREIGN KEY (profession_id)   REFERENCES professions (id) ON DELETE SET NULL,
  CONSTRAINT fk_appointments_service      FOREIGN KEY (service_id)      REFERENCES services (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- At most one review per appointment, written by that appointment's client.
CREATE TABLE reviews (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  appointment_id  BIGINT UNSIGNED NOT NULL,
  reviewer_id     BIGINT UNSIGNED NOT NULL,
  professional_id BIGINT UNSIGNED NOT NULL,
  profession_id   BIGINT UNSIGNED NULL,
  rating          TINYINT UNSIGNED NOT NULL,
  comment         TEXT NULL,
  created_at      DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_reviews_appointment (appointment_id),
  KEY ix_reviews_professional (professional_id, created_at),
  CONSTRAINT ck_reviews_rating CHECK (rating BETWEEN 1 AND 5),
  CONSTRAINT fk_reviews_appointment  FOREIGN KEY (appointment_id)  REFERENCES appointments (id),
  CONSTRAINT fk_reviews_reviewer     FOREIGN KEY (reviewer_id)     REFERENCES users (id),
  CONSTRAINT fk_reviews_professional FOREIGN KEY (professional_id) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A room is a pair of users; (user_a_id < user_b_id) makes the pair unique regardless of who started it.
CREATE TABLE chat_rooms (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_a_id       BIGINT UNSIGNED NOT NULL,
  user_b_id       BIGINT UNSIGNED NOT NULL,
  last_message_at DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  created_at      DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_chat_rooms_pair (user_a_id, user_b_id),
  KEY ix_chat_rooms_b (user_b_id),
  CONSTRAINT ck_chat_rooms_order CHECK (user_a_id < user_b_id),
  CONSTRAINT fk_chat_rooms_a FOREIGN KEY (user_a_id) REFERENCES users (id) ON DELETE CASCADE,
  CONSTRAINT fk_chat_rooms_b FOREIGN KEY (user_b_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE chat_messages (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  room_id      BIGINT UNSIGNED NOT NULL,
  sender_id    BIGINT UNSIGNED NOT NULL,
  message_type ENUM('text','location') NOT NULL DEFAULT 'text',
  content      TEXT NULL,
  latitude     DECIMAL(10,7) NULL,
  longitude    DECIMAL(10,7) NULL,
  is_read      TINYINT(1) NOT NULL DEFAULT 0,
  created_at   DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_chat_messages_room (room_id, id),
  KEY ix_chat_messages_unread (room_id, is_read, sender_id),
  CONSTRAINT fk_chat_messages_room   FOREIGN KEY (room_id)   REFERENCES chat_rooms (id) ON DELETE CASCADE,
  CONSTRAINT fk_chat_messages_sender FOREIGN KEY (sender_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE alerts (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id         BIGINT UNSIGNED NOT NULL,
  name            VARCHAR(100) NOT NULL,
  search_criteria JSON NOT NULL,
  frequency       ENUM('daily','weekly','monthly') NOT NULL DEFAULT 'daily',
  created_at      DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_alerts_user (user_id),
  CONSTRAINT fk_alerts_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
