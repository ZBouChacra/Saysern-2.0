-- Removes a database and its two accounts. Used by the test suite to clean up the throw-away
-- databases it creates; also available as `Sayserne.Migrator deprovision` for local resets.
-- DESTRUCTIVE: drops all data in `{{DB_NAME}}`.

DROP DATABASE IF EXISTS `{{DB_NAME}}`;
DROP USER IF EXISTS '{{MIGRATOR_USER}}'@'{{USER_HOST}}';
DROP USER IF EXISTS '{{APP_USER}}'@'{{USER_HOST}}';
