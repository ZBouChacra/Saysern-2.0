-- V005: configuration platform (settings, lookups, translations), audit log, roles/permissions, two-factor.

-- Runtime settings. Definitions (type, default, min, max) are data shipped by migrations; admins change only value_text,
-- and only within [min_value, max_value]. is_public settings are exposed to the web app (e.g. password rules).
CREATE TABLE app_settings (
  setting_key  VARCHAR(100)  NOT NULL,
  group_name   VARCHAR(50)   NOT NULL,
  value_type   ENUM('int','decimal','bool','string') NOT NULL,
  value_text   VARCHAR(2000) NOT NULL,
  default_text VARCHAR(2000) NOT NULL,
  min_value    DECIMAL(18,4) NULL,
  max_value    DECIMAL(18,4) NULL,
  description  VARCHAR(500)  NOT NULL,
  is_public    TINYINT(1)    NOT NULL DEFAULT 0,
  updated_at   DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_by   BIGINT UNSIGNED NULL,
  PRIMARY KEY (setting_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Lookups: typed lists (countries, nationalities, ...) with optional parent (cities -> country), ordering, active flag,
-- free-form metadata and one translated name per language.
CREATE TABLE lookup_types (
  code           VARCHAR(50)  NOT NULL,
  name           VARCHAR(100) NOT NULL,
  description    VARCHAR(300) NULL,
  parent_type    VARCHAR(50)  NULL,
  is_system      TINYINT(1)   NOT NULL DEFAULT 0,
  PRIMARY KEY (code),
  CONSTRAINT fk_lookup_types_parent FOREIGN KEY (parent_type) REFERENCES lookup_types (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE lookup_values (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  type_code   VARCHAR(50)  NOT NULL,
  code        VARCHAR(100) NOT NULL,
  parent_id   BIGINT UNSIGNED NULL,
  sort_order  INT          NOT NULL DEFAULT 0,
  is_active   TINYINT(1)   NOT NULL DEFAULT 1,
  metadata    JSON         NULL,
  created_at  DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_lookup_values_type_code (type_code, code),
  KEY ix_lookup_values_parent (parent_id),
  CONSTRAINT fk_lookup_values_type   FOREIGN KEY (type_code) REFERENCES lookup_types (code),
  CONSTRAINT fk_lookup_values_parent FOREIGN KEY (parent_id)  REFERENCES lookup_values (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE lookup_translations (
  value_id BIGINT UNSIGNED NOT NULL,
  language VARCHAR(10)  NOT NULL,
  name     VARCHAR(200) NOT NULL,
  PRIMARY KEY (value_id, language),
  CONSTRAINT fk_lookup_translations_value FOREIGN KEY (value_id) REFERENCES lookup_values (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- UI texts. default_value is what the migration shipped, so an admin edit can be reset.
CREATE TABLE translations (
  language      VARCHAR(10)  NOT NULL,
  translation_key VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,   -- case-sensitive: 'ACCOUNT_LOCKED' and 'account_locked' are different keys
  value         TEXT         NOT NULL,
  default_value TEXT         NOT NULL,
  updated_at    DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_by    BIGINT UNSIGNED NULL,
  PRIMARY KEY (language, translation_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Append-only: the API's account can only EXECUTE procedures, and there is no procedure that updates or deletes a row.
CREATE TABLE audit_log (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  occurred_at DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  actor_id    BIGINT UNSIGNED NULL,
  actor_name  VARCHAR(255) NULL,
  action      VARCHAR(100) NOT NULL,
  entity_type VARCHAR(50)  NOT NULL,
  entity_id   VARCHAR(100) NULL,
  summary     VARCHAR(500) NOT NULL,
  details     JSON         NULL,
  ip          VARCHAR(45)  NULL,
  PRIMARY KEY (id),
  KEY ix_audit_time (occurred_at),
  KEY ix_audit_actor (actor_id),
  KEY ix_audit_entity (entity_type, entity_id),
  KEY ix_audit_action (action)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Permissions are defined by the code that checks them (each endpoint names one); roles are data.
CREATE TABLE permissions (
  code        VARCHAR(100) NOT NULL,
  group_name  VARCHAR(50)  NOT NULL,
  description VARCHAR(300) NOT NULL,
  PRIMARY KEY (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE roles (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  name        VARCHAR(100) NOT NULL,
  description VARCHAR(300) NULL,
  created_at  DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_roles_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE role_permissions (
  role_id         BIGINT UNSIGNED NOT NULL,
  permission_code VARCHAR(100) NOT NULL,
  PRIMARY KEY (role_id, permission_code),
  CONSTRAINT fk_role_permissions_role FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE CASCADE,
  CONSTRAINT fk_role_permissions_perm FOREIGN KEY (permission_code) REFERENCES permissions (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE user_roles (
  user_id BIGINT UNSIGNED NOT NULL,
  role_id BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (user_id, role_id),
  CONSTRAINT fk_user_roles_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
  CONSTRAINT fk_user_roles_role FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- TOTP secret (encrypted by the API with ASP.NET Data Protection) and single-use recovery codes (SHA-256 hashes only).
CREATE TABLE user_two_factor (
  user_id          BIGINT UNSIGNED NOT NULL,
  secret_protected VARCHAR(512) NOT NULL,
  confirmed_at     DATETIME(3)  NULL,
  last_used_step   BIGINT       NULL,
  created_at       DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (user_id),
  CONSTRAINT fk_two_factor_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE user_recovery_codes (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id    BIGINT UNSIGNED NOT NULL,
  code_hash  CHAR(64)    NOT NULL,
  used_at    DATETIME(3) NULL,
  created_at DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_recovery_user (user_id),
  CONSTRAINT fk_recovery_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Enumerations that are really lookups become plain codes validated against lookup_values.
ALTER TABLE users  MODIFY sex VARCHAR(30) NULL, MODIFY preferred_language VARCHAR(10) NOT NULL DEFAULT 'en';
ALTER TABLE alerts MODIFY frequency VARCHAR(20) NOT NULL DEFAULT 'daily';
