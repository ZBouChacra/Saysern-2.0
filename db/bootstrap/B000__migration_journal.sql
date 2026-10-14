-- Bootstrap: the migration journal itself. Idempotent; run on every migrate.
-- Everything the migrator does after this point goes through stored procedures.

CREATE TABLE IF NOT EXISTS schema_migrations (
  id          INT UNSIGNED NOT NULL AUTO_INCREMENT,
  kind        ENUM('versioned','repeatable') NOT NULL,
  version     VARCHAR(50)  NULL,
  name        VARCHAR(200) NOT NULL,
  checksum    CHAR(64)     NOT NULL,
  applied_at  DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_schema_migrations_kind_name (kind, name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS sp_schema_migrations_list;
DROP PROCEDURE IF EXISTS sp_schema_migrations_record;
DROP PROCEDURE IF EXISTS sp_schema_migrations_lock;
DROP PROCEDURE IF EXISTS sp_schema_migrations_unlock;

DELIMITER $$

CREATE PROCEDURE sp_schema_migrations_list()
BEGIN
  SELECT kind, name, checksum FROM schema_migrations ORDER BY id;
END$$

CREATE PROCEDURE sp_schema_migrations_record(
  IN p_kind VARCHAR(20), IN p_version VARCHAR(50), IN p_name VARCHAR(200), IN p_checksum CHAR(64))
BEGIN
  INSERT INTO schema_migrations (kind, version, name, checksum)
  VALUES (p_kind, p_version, p_name, p_checksum)
  ON DUPLICATE KEY UPDATE checksum = VALUES(checksum), applied_at = UTC_TIMESTAMP(3);
END$$

-- Serialises concurrent migrators (advisory lock is held by the migrator's connection).
CREATE PROCEDURE sp_schema_migrations_lock()
BEGIN
  SELECT GET_LOCK('sayserne_schema_migrations', 60) AS acquired;
END$$

CREATE PROCEDURE sp_schema_migrations_unlock()
BEGIN
  SELECT RELEASE_LOCK('sayserne_schema_migrations') AS released;
END$$

DELIMITER ;
