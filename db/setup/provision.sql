-- One-off provisioning, run by `Sayserne.Migrator provision` with a privileged (admin) connection.
-- {{PLACEHOLDERS}} are substituted by the migrator after strict validation.
-- This is database administration, not application code.
--
--   migrator user : owns the schema (DDL, creates/changes stored procedures)
--   app user      : may ONLY execute stored procedures. No SELECT/INSERT/UPDATE/DELETE on any table.

CREATE DATABASE IF NOT EXISTS `{{DB_NAME}}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER IF NOT EXISTS '{{MIGRATOR_USER}}'@'{{USER_HOST}}' IDENTIFIED BY '{{MIGRATOR_PASSWORD}}';
CREATE USER IF NOT EXISTS '{{APP_USER}}'@'{{USER_HOST}}' IDENTIFIED BY '{{APP_PASSWORD}}';

GRANT ALL PRIVILEGES ON `{{DB_NAME}}`.* TO '{{MIGRATOR_USER}}'@'{{USER_HOST}}';
GRANT EXECUTE ON `{{DB_NAME}}`.* TO '{{APP_USER}}'@'{{USER_HOST}}';
