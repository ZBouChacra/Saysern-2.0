-- V006: settings, permissions, starter roles and the small lookups. Everything here is data an admin can maintain.

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Auth.PasswordMinLength',      'Authentication', 'int',  '10',   '10',   8,    128,  1, 'Minimum password length for form-based accounts (Google sign-in is unaffected). Cannot go below 8.'),
 ('Auth.PasswordRequireLetter',  'Authentication', 'bool', 'true', 'true', NULL, NULL, 1, 'Passwords must contain at least one letter.'),
 ('Auth.PasswordRequireDigit',   'Authentication', 'bool', 'true', 'true', NULL, NULL, 1, 'Passwords must contain at least one digit.'),
 ('Auth.MaxFailedAttempts',      'Authentication', 'int',  '5',    '5',    3,    20,   0, 'Consecutive wrong passwords before a customer or professional account is locked temporarily.'),
 ('Auth.LockoutMinutes',         'Authentication', 'int',  '15',   '15',   1,    1440, 0, 'How long a temporary lockout lasts (minutes).'),
 ('Auth.SessionHours',           'Authentication', 'int',  '8',    '8',    1,    720,  0, 'Length of a customer/professional session (hours).'),
 ('Auth.AdminMaxFailedAttempts', 'Administration', 'int', '3',    '3',    2,    10,   0, 'Consecutive wrong passwords or codes before an admin account is locked temporarily.'),
 ('Auth.AdminLockoutMinutes',    'Administration', 'int', '30',   '30',   5,    1440, 0, 'How long an admin lockout lasts (minutes).'),
 ('Auth.AdminIdleMinutes',       'Administration', 'int', '30',   '30',   5,    240,  0, 'An admin session ends after this many minutes without activity.'),
 ('Auth.AdminMaxSessionHours',   'Administration', 'int', '12',   '12',   1,    24,   0, 'Absolute lifetime of an admin session (hours), regardless of activity.'),
 ('Auth.AuthRequestsPerMinute',  'Rate limits',    'int', '30',   '30',   5,    1000, 0, 'Sign-in, registration and similar requests allowed per client per minute.'),
 ('Auth.WritesPerMinute',        'Rate limits',    'int', '120',  '120',  10,   5000, 0, 'Writes (profile, booking, chat, alerts) allowed per user per minute.'),
 ('Booking.MinDurationMinutes',  'Booking',        'int', '15',   '15',   5,    120,  1, 'Shortest appointment that can be booked (minutes).'),
 ('Booking.MaxDurationMinutes',  'Booking',        'int', '480',  '480',  30,   1440, 1, 'Longest appointment that can be booked (minutes).'),
 ('Booking.MaxDescriptionLength','Booking',        'int', '1000', '1000', 100,  5000, 1, 'Maximum length of the note on a booking request.'),
 ('Chat.MaxMessageLength',       'Chat',           'int', '4000', '4000', 100,  20000,1, 'Maximum length of a chat message.'),
 ('Alerts.MaxPerUser',           'Saved searches', 'int', '20',   '20',   1,    200,  1, 'How many saved searches a user may keep.'),
 ('Search.DefaultPageSize',      'Search',         'int', '20',   '20',   5,    100,  1, 'Results per page when the caller does not say.'),
 ('Search.MaxPageSize',          'Search',         'int', '50',   '50',   10,   200,  1, 'Largest page a caller may ask for.'),
 ('Reviews.MaxCommentLength',    'Reviews',        'int', '2000', '2000', 100,  10000,1, 'Maximum length of a review comment.'),
 ('Profile.MaxGeographicAreas',  'Profiles',       'int', '30',   '30',   1,    100,  1, 'How many areas a professional may list per service.'),
 ('Profile.MaxBioLength',        'Profiles',       'int', '2000', '2000', 100,  10000,1, 'Maximum length of the profile "about" text.'),
 ('Site.Name',                   'Site',           'string','SaySerné','SaySerné', NULL, NULL, 1, 'Site name shown to visitors and used as the issuer in authenticator apps.'),
 ('Site.DefaultLanguage',        'Site',           'string','en', 'en',   NULL, NULL, 1, 'Language shown to visitors who have not chosen one (must exist in the language lookup).');

INSERT INTO permissions (code, group_name, description) VALUES
 ('users.read',          'Users',          'See the user list and user details.'),
 ('users.manage',        'Users',          'Lock or unlock users and set their premium / starred flags.'),
 ('admins.manage',       'Users',          'Grant or remove super-admin, and reset an admin''s two-factor sign-in.'),
 ('roles.manage',        'Users',          'Create and edit roles, and assign roles to users.'),
 ('settings.read',       'Configuration',  'See runtime settings.'),
 ('settings.manage',     'Configuration',  'Change runtime settings.'),
 ('lookups.manage',      'Configuration',  'Edit lookups (countries, cities, nationalities, ...).'),
 ('translations.manage', 'Configuration',  'Edit UI texts and their translations.'),
 ('catalog.manage',      'Catalog',        'Create and edit categories and services.'),
 ('audit.read',          'Audit',          'Read the audit log.');

INSERT INTO roles (name, description) VALUES
 ('Support',    'Look users up and read the audit log.'),
 ('Operations', 'Manage users and read settings.'),
 ('Content',    'Maintain the catalog, lookups and translations.');
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON
  (r.name = 'Support'    AND p.code IN ('users.read', 'audit.read')) OR
  (r.name = 'Operations' AND p.code IN ('users.read', 'users.manage', 'settings.read', 'audit.read')) OR
  (r.name = 'Content'    AND p.code IN ('catalog.manage', 'lookups.manage', 'translations.manage'));

INSERT INTO lookup_types (code, name, description, parent_type, is_system) VALUES
 ('language',          'Languages',          'Languages the site is available in.', NULL, 1),
 ('country',           'Countries',          'Countries (ISO 3166-1 alpha-2 codes).', NULL, 1),
 ('city',              'Cities',             'Cities, each belonging to a country.', 'country', 1),
 ('nationality',       'Nationalities',      'Nationalities offered on profiles.', NULL, 1),
 ('sex',               'Sex',                'Values offered for the sex field.', NULL, 1),
 ('alert_frequency',   'Alert frequencies',  'How often a saved search is delivered.', NULL, 1),
 ('currency',          'Currencies',         'Currencies prices can be expressed in.', NULL, 1),
 ('duration_option',   'Appointment lengths','Appointment lengths offered when booking (code = minutes).', NULL, 1),
 ('weekday',           'Weekdays',           'Day names (code 0 = Sunday).', NULL, 1),
 ('appointment_status','Appointment statuses','Labels and colors for appointment statuses (transitions are fixed in code).', NULL, 1),
 ('message_type',      'Message types',      'Kinds of chat message.', NULL, 1),
 ('profile_type',      'Profile types',      'Customer or professional.', NULL, 1);

-- staging table: type, code, sort, metadata, English name, Arabic name
CREATE TEMPORARY TABLE tmp_seed (type_code VARCHAR(50), code VARCHAR(100), sort_order INT, metadata JSON, name_en VARCHAR(200), name_ar VARCHAR(200));
INSERT INTO tmp_seed VALUES
 ('language','en',1,'{"dir":"ltr","native":"English"}','English','الإنجليزية'),
 ('language','ar',2,'{"dir":"rtl","native":"العربية"}','Arabic','العربية'),
 ('sex','male',1,NULL,'Male','ذكر'), ('sex','female',2,NULL,'Female','أنثى'),
 ('alert_frequency','daily',1,NULL,'Daily','يومي'), ('alert_frequency','weekly',2,NULL,'Weekly','أسبوعي'), ('alert_frequency','monthly',3,NULL,'Monthly','شهري'),
 ('currency','USD',1,'{"symbol":"$","decimals":2}','US Dollar','دولار أمريكي'),
 ('duration_option','30',1,NULL,'30 minutes','30 دقيقة'), ('duration_option','45',2,NULL,'45 minutes','45 دقيقة'), ('duration_option','60',3,NULL,'1 hour','ساعة واحدة'),
 ('duration_option','90',4,NULL,'1.5 hours','ساعة ونصف'), ('duration_option','120',5,NULL,'2 hours','ساعتان'), ('duration_option','180',6,NULL,'3 hours','3 ساعات'),
 ('weekday','0',0,NULL,'Sunday','الأحد'), ('weekday','1',1,NULL,'Monday','الاثنين'), ('weekday','2',2,NULL,'Tuesday','الثلاثاء'), ('weekday','3',3,NULL,'Wednesday','الأربعاء'),
 ('weekday','4',4,NULL,'Thursday','الخميس'), ('weekday','5',5,NULL,'Friday','الجمعة'), ('weekday','6',6,NULL,'Saturday','السبت'),
 ('appointment_status','pending',1,'{"color":"amber"}','Pending','قيد الانتظار'), ('appointment_status','approved',2,'{"color":"green"}','Approved','مؤكد'),
 ('appointment_status','completed',3,'{"color":"green"}','Completed','مكتمل'), ('appointment_status','cancelled',4,'{"color":"red"}','Cancelled','ملغى'),
 ('message_type','text',1,NULL,'Text','نص'), ('message_type','location',2,NULL,'Location','موقع'),
 ('profile_type','customer',1,NULL,'Customer','عميل'), ('profile_type','professional',2,NULL,'Professional','محترف');
INSERT INTO lookup_values (type_code, code, sort_order, metadata) SELECT type_code, code, sort_order, metadata FROM tmp_seed;
-- (a temporary table cannot be used twice in one statement, hence two inserts)
INSERT INTO lookup_translations (value_id, language, name)
  SELECT v.id, 'en', s.name_en FROM tmp_seed s JOIN lookup_values v ON v.type_code = s.type_code AND v.code = s.code;
INSERT INTO lookup_translations (value_id, language, name)
  SELECT v.id, 'ar', s.name_ar FROM tmp_seed s JOIN lookup_values v ON v.type_code = s.type_code AND v.code = s.code;
DROP TEMPORARY TABLE tmp_seed;
