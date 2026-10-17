-- V022: contact messages from the public site, an inbox for staff, and replies by e-mail.

CREATE TABLE contact_messages (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id       BIGINT UNSIGNED NULL,
  name          VARCHAR(100)  NOT NULL,
  email         VARCHAR(255)  NOT NULL,
  language      VARCHAR(10)   NOT NULL,
  topic         VARCHAR(40)   NULL,
  subject       VARCHAR(150)  NOT NULL,
  body          TEXT          NOT NULL,
  status        ENUM('new','in_progress','replied','archived','spam') NOT NULL DEFAULT 'new',
  reply_body    TEXT          NULL,
  replied_at    DATETIME(3)   NULL,
  replied_by    BIGINT UNSIGNED NULL,
  internal_note VARCHAR(1000) NULL,
  created_at    DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at    DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  KEY ix_contact_status (status, created_at),
  KEY ix_contact_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Contact.MaxMessageLength', 'Contact', 'int',    '4000', '4000', 100, 20000, 1, 'The longest message the contact form accepts.'),
 ('Contact.MessagesPerHour',  'Contact', 'int',    '10',   '10',   1,   200,   0, 'How many contact form submissions one address may make per hour, including ones refused for a mistake (the rest are refused).'),
 ('Contact.NotifyAddress',    'Contact', 'string', '',     '',     NULL, NULL, 0, 'An e-mail address to be told when a contact message arrives (leave empty for none). Needs e-mail switched on.');
INSERT INTO permissions (code, group_name, description) VALUES
 ('contact.read',   'Contact', 'Read contact messages.'),
 ('contact.manage', 'Contact', 'Reply to contact messages and change their status.');

INSERT INTO lookup_types (code, name, description, is_system) VALUES ('contact_topic', 'Contact topics', 'What a contact message is about (offered on the contact form).', 0);
CREATE TEMPORARY TABLE tmp_seed (code VARCHAR(100), sort_order INT, name_en VARCHAR(200), name_ar VARCHAR(200));
INSERT INTO tmp_seed VALUES
 ('general',     1, 'A general question',      'سؤال عام'),
 ('support',     2, 'Help with my account',     'مساعدة بخصوص حسابي'),
 ('listing',     3, 'Listing my services',      'عرض خدماتي'),
 ('advertising', 4, 'Advertising or premium',   'الإعلانات أو التميّز'),
 ('report',      5, 'Report a problem or abuse','الإبلاغ عن مشكلة أو إساءة');
INSERT INTO lookup_values (type_code, code, sort_order) SELECT 'contact_topic', code, sort_order FROM tmp_seed;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'en', s.name_en FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'contact_topic' AND v.code = s.code;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'ar', s.name_ar FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'contact_topic' AND v.code = s.code;
DROP TEMPORARY TABLE tmp_seed;
