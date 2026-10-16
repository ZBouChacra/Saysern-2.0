-- V013: saved-search digests.
ALTER TABLE alerts ADD COLUMN last_digest_at DATETIME(3) NULL;

-- How long each frequency is lives in the lookup (metadata.days), so an admin can add "every 3 days" without a deployment.
UPDATE lookup_values SET metadata = JSON_OBJECT('days', 1)  WHERE type_code = 'alert_frequency' AND code = 'daily';
UPDATE lookup_values SET metadata = JSON_OBJECT('days', 7)  WHERE type_code = 'alert_frequency' AND code = 'weekly';
UPDATE lookup_values SET metadata = JSON_OBJECT('days', 30) WHERE type_code = 'alert_frequency' AND code = 'monthly';

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Alerts.DigestMaxListings', 'Saved searches', 'int', '10', '10', 1, 50, 0, 'How many new professionals a saved-search e-mail lists (the e-mail always says how many there are in total).');
