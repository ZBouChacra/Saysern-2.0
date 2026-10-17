-- V027: the site's look: colours and logo, set in the back office. The defaults are the original colours, so nothing changes until an admin chooses.

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Brand.PrimaryColor',     'Brand', 'string', '#2d6d5f', '#2d6d5f', NULL, NULL, 1, 'The main colour of the site (buttons and links), as #rrggbb. It must be dark enough for white text on it.'),
 ('Brand.PrimaryDarkColor', 'Brand', 'string', '#1f4d43', '#1f4d43', NULL, NULL, 1, 'A darker shade of the main colour (hovering over buttons), as #rrggbb.'),
 ('Brand.AccentColor',      'Brand', 'string', '#d4a757', '#d4a757', NULL, NULL, 1, 'The accent colour (small highlights), as #rrggbb.'),
 ('Brand.LogoFileId',       'Brand', 'string', '',        '',        NULL, NULL, 0, 'The uploaded logo shown in the header instead of the site name (set it from the Brand page).');
