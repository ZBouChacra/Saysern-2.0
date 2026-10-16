-- V011: e-mail (provider settings, outbox, single-use tokens) and per-user notification preferences.

ALTER TABLE app_settings MODIFY value_type ENUM('int','decimal','bool','string','secret') NOT NULL;

-- Verification and password-reset tokens. Only the SHA-256 of a token is stored, so a database leak cannot be used to take over accounts.
CREATE TABLE user_tokens (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id    BIGINT UNSIGNED NOT NULL,
  purpose    ENUM('verify_email','reset_password') NOT NULL,
  token_hash CHAR(64)    NOT NULL,
  expires_at DATETIME(3) NOT NULL,
  used_at    DATETIME(3) NULL,
  created_at DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_user_tokens_hash (token_hash),
  KEY ix_user_tokens_user (user_id, purpose, created_at),
  CONSTRAINT fk_user_tokens_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One row per e-mail. The message text is kept only until it has been sent (it may contain a link that signs someone in or resets a password).
CREATE TABLE email_outbox (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id    BIGINT UNSIGNED NULL,
  to_address VARCHAR(255) NOT NULL,
  event_code VARCHAR(60)  NOT NULL,
  language   VARCHAR(10)  NOT NULL,
  subject    VARCHAR(300) NOT NULL,
  body_text  MEDIUMTEXT   NULL,
  body_html  MEDIUMTEXT   NULL,
  status     ENUM('queued','sent','failed','skipped') NOT NULL DEFAULT 'queued',
  attempts   INT          NOT NULL DEFAULT 0,
  last_error VARCHAR(1000) NULL,
  created_at DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  sent_at    DATETIME(3)  NULL,
  PRIMARY KEY (id),
  KEY ix_email_outbox_status (status, created_at),
  KEY ix_email_outbox_user (user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A user's explicit choices. No row = the default (on). Mandatory events (verification, password reset) cannot be switched off.
CREATE TABLE notification_preferences (
  user_id    BIGINT UNSIGNED NOT NULL,
  event_code VARCHAR(60) NOT NULL,
  channel    VARCHAR(20) NOT NULL,
  enabled    TINYINT(1)  NOT NULL,
  PRIMARY KEY (user_id, event_code, channel),
  CONSTRAINT fk_notification_prefs_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Email.Provider',        'Email', 'string', 'off',      'off',      NULL, NULL, 0, 'How e-mail is sent: "smtp" (a real mail server) or "off" (nothing is sent; verification cannot then be required).'),
 ('Email.FromAddress',     'Email', 'string', 'no-reply@localhost', 'no-reply@localhost', NULL, NULL, 0, 'The sender address on every e-mail. Use an address on your own domain so SPF/DKIM can vouch for it.'),
 ('Email.FromName',        'Email', 'string', 'SaySerné', 'SaySerné', NULL, NULL, 0, 'The sender name shown in the recipient''s inbox.'),
 ('Email.SmtpHost',        'Email', 'string', '',         '',         NULL, NULL, 0, 'The mail server''s host name (given by your e-mail provider).'),
 ('Email.SmtpPort',        'Email', 'int',    '587',      '587',      1,    65535, 0, 'The mail server''s port (587 for STARTTLS, 25 for plain).'),
 ('Email.SmtpSecurity',    'Email', 'string', 'starttls', 'starttls', NULL, NULL, 0, 'Connection security: "starttls" (recommended, port 587) or "none". Implicit TLS on port 465 is not supported.'),
 ('Email.SmtpUsername',    'Email', 'string', '',         '',         NULL, NULL, 0, 'The mail server login (leave empty if it needs none).'),
 ('Email.SmtpPassword',    'Email', 'secret', '',         '',         NULL, NULL, 0, 'The mail server password. Stored encrypted and never shown again; leave blank to clear it.'),
 ('Site.PublicUrl',        'Site',  'string', 'http://localhost:5173', 'http://localhost:5173', NULL, NULL, 0, 'The public address of the website, used to build the links in e-mails (no trailing slash).'),
 ('Email.VerifyTokenHours','Email', 'int',    '48',       '48',       1,    720,  0, 'How long an e-mail verification link stays valid (hours).'),
 ('Email.ResetTokenMinutes','Email','int',    '60',       '60',       10,   1440, 0, 'How long a password-reset link stays valid (minutes).'),
 ('Email.MaxTokenEmailsPerHour','Email','int','5',        '5',        1,    50,   0, 'How many verification / reset e-mails one account may request per hour.'),
 ('Email.MessageDelayMinutes','Email','int',  '10',       '10',       1,    1440, 0, 'A "new message" e-mail waits this long and is skipped if the message was read meanwhile.'),
 ('Auth.RequireVerifiedEmail','Authentication','bool','true','true', NULL, NULL, 1, 'Form-registered users must verify their e-mail before booking or messaging. Only takes effect while e-mail is switched on.');
INSERT INTO permissions (code, group_name, description) VALUES ('email.read', 'Operations', 'See which e-mails were sent or failed (never their content).');

INSERT INTO lookup_types (code, name, description, is_system) VALUES
 ('email_provider',       'E-mail providers',      'Ways e-mail can be sent.', 1),
 ('smtp_security',        'SMTP security',         'Connection security for the mail server.', 1),
 ('notification_event',   'Notification events',   'Things the site can e-mail a user about. Metadata: {"mandatory":true} cannot be switched off.', 1),
 ('notification_channel', 'Notification channels', 'Ways a user can be notified. Channels that are switched off are not offered.', 1);
CREATE TEMPORARY TABLE tmp_seed (type_code VARCHAR(50), code VARCHAR(100), sort_order INT, is_active TINYINT, metadata JSON, name_en VARCHAR(200), name_ar VARCHAR(200));
INSERT INTO tmp_seed VALUES
 ('email_provider','smtp',1,1,NULL,'SMTP mail server','خادم بريد SMTP'), ('email_provider','off',2,1,NULL,'Off (send nothing)','متوقف (لا يُرسل شيء)'),
 ('smtp_security','starttls',1,1,NULL,'STARTTLS (recommended)','STARTTLS (موصى به)'), ('smtp_security','none',2,1,NULL,'None','بدون تشفير'),
 ('notification_event','verify_email',1,1,'{"mandatory":true}','Confirm your e-mail address','تأكيد بريدك الإلكتروني'),
 ('notification_event','password_reset',2,1,'{"mandatory":true}','Password reset','إعادة تعيين كلمة المرور'),
 ('notification_event','booking_requested',3,1,NULL,'Someone requests an appointment with me','طلب موعد جديد معي'),
 ('notification_event','booking_approved',4,1,NULL,'My appointment is approved','الموافقة على موعدي'),
 ('notification_event','booking_cancelled',5,1,NULL,'An appointment is cancelled','إلغاء موعد'),
 ('notification_event','booking_completed',6,1,NULL,'An appointment is completed','اكتمال موعد'),
 ('notification_event','new_message',7,1,NULL,'I receive a new message','وصول رسالة جديدة'),
 ('notification_event','review_received',8,1,NULL,'I receive a review','وصول تقييم جديد'),
 ('notification_event','alert_digest',9,1,NULL,'Saved-search updates','تحديثات عمليات البحث المحفوظة'),
 ('notification_channel','email',1,1,NULL,'E-mail','البريد الإلكتروني'),
 ('notification_channel','sms',2,0,NULL,'SMS','رسائل SMS'),
 ('notification_channel','whatsapp',3,0,NULL,'WhatsApp','واتساب');
INSERT INTO lookup_values (type_code, code, sort_order, is_active, metadata) SELECT type_code, code, sort_order, is_active, metadata FROM tmp_seed;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'en', s.name_en FROM tmp_seed s JOIN lookup_values v ON v.type_code = s.type_code AND v.code = s.code;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'ar', s.name_ar FROM tmp_seed s JOIN lookup_values v ON v.type_code = s.type_code AND v.code = s.code;
DROP TEMPORARY TABLE tmp_seed;
