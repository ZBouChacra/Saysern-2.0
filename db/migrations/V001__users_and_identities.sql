-- V001: accounts. A user may have a local password, one or more external identities, or both.

CREATE TABLE users (
  id                    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  email                 VARCHAR(255)    NOT NULL,
  email_verified        TINYINT(1)      NOT NULL DEFAULT 0,
  password_hash         VARCHAR(255)    NULL,
  first_name            VARCHAR(100)    NOT NULL,
  last_name             VARCHAR(100)    NOT NULL,
  role                  ENUM('user','admin')             NOT NULL DEFAULT 'user',
  profile_type          ENUM('customer','professional')  NOT NULL DEFAULT 'customer',
  is_locked             TINYINT(1)      NOT NULL DEFAULT 0,
  failed_login_attempts INT UNSIGNED    NOT NULL DEFAULT 0,
  locked_until          DATETIME(3)     NULL,
  created_at            DATETIME(3)     NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  last_signed_in_at     DATETIME(3)     NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_users_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE user_identities (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id         BIGINT UNSIGNED NOT NULL,
  provider        VARCHAR(30)     NOT NULL,
  subject         VARCHAR(255)    NOT NULL,
  email           VARCHAR(255)    NULL,
  email_verified  TINYINT(1)      NOT NULL DEFAULT 0,
  created_at      DATETIME(3)     NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_identity_provider_subject (provider, subject),
  UNIQUE KEY uq_identity_user_provider (user_id, provider),
  CONSTRAINT fk_identity_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
