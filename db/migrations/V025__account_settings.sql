-- V025: account settings: time zone, disabling an account (never deleting it) and re-enabling it by e-mail confirmation.

ALTER TABLE users
  ADD COLUMN time_zone   VARCHAR(60) NULL,
  ADD COLUMN is_disabled TINYINT(1) NOT NULL DEFAULT 0,
  ADD COLUMN disabled_at DATETIME(3) NULL;
ALTER TABLE user_tokens MODIFY purpose ENUM('verify_email','reset_password','reenable_account') NOT NULL;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Site.DefaultTimeZone', 'Site', 'string', 'Asia/Beirut', 'Asia/Beirut', NULL, NULL, 1, 'The time zone used for times in e-mails and receipts when a person has not chosen one (an IANA name such as Asia/Beirut).');

INSERT INTO lookup_values (type_code, code, sort_order, is_active, metadata) VALUES
 ('notification_event', 'reenable_account', 12, 1, '{"mandatory":true}'),
 ('notification_event', 'password_changed', 13, 1, '{"mandatory":true}');
INSERT INTO lookup_translations (value_id, language, name)
SELECT v.id, t.language, t.name FROM lookup_values v JOIN (
  SELECT 'reenable_account' code, 'en' language, 'Re-enabling my account' name UNION ALL SELECT 'reenable_account', 'ar', 'إعادة تفعيل حسابي'
  UNION ALL SELECT 'password_changed', 'en', 'My password was changed' UNION ALL SELECT 'password_changed', 'ar', 'تم تغيير كلمة مروري') t
  ON t.code = v.code WHERE v.type_code = 'notification_event';

INSERT INTO lookup_types (code, name, description, is_system) VALUES ('time_zone', 'Time zones', 'The time zones a person may choose (IANA names). Used for times in e-mails and receipts.', 0);
CREATE TEMPORARY TABLE tmp_seed (code VARCHAR(60), sort_order INT, name_en VARCHAR(120), name_ar VARCHAR(120));
INSERT INTO tmp_seed VALUES
 ('Asia/Beirut',1,'Beirut','بيروت'),('Asia/Riyadh',2,'Riyadh','الرياض'),('Asia/Dubai',3,'Dubai','دبي'),('Asia/Kuwait',4,'Kuwait','الكويت'),('Asia/Qatar',5,'Doha','الدوحة'),
 ('Asia/Baghdad',6,'Baghdad','بغداد'),('Asia/Amman',7,'Amman','عمّان'),('Asia/Damascus',8,'Damascus','دمشق'),('Asia/Jerusalem',9,'Jerusalem','القدس'),('Africa/Cairo',10,'Cairo','القاهرة'),
 ('Africa/Casablanca',11,'Casablanca','الدار البيضاء'),('Africa/Tunis',12,'Tunis','تونس'),('Africa/Algiers',13,'Algiers','الجزائر'),('Africa/Tripoli',14,'Tripoli','طرابلس'),
 ('Europe/London',20,'London','لندن'),('Europe/Paris',21,'Paris','باريس'),('Europe/Berlin',22,'Berlin','برلين'),('Europe/Rome',23,'Rome','روما'),('Europe/Madrid',24,'Madrid','مدريد'),
 ('Europe/Istanbul',25,'Istanbul','إسطنبول'),('Europe/Moscow',26,'Moscow','موسكو'),
 ('America/New_York',30,'New York','نيويورك'),('America/Chicago',31,'Chicago','شيكاغو'),('America/Denver',32,'Denver','دنفر'),('America/Los_Angeles',33,'Los Angeles','لوس أنجلوس'),
 ('America/Toronto',34,'Toronto','تورونتو'),('America/Sao_Paulo',35,'São Paulo','ساو باولو'),
 ('Africa/Lagos',40,'Lagos','لاغوس'),('Africa/Johannesburg',41,'Johannesburg','جوهانسبرغ'),('Africa/Nairobi',42,'Nairobi','نيروبي'),
 ('Asia/Kolkata',50,'Mumbai / Delhi','مومباي / دلهي'),('Asia/Shanghai',51,'Beijing / Shanghai','بكين / شنغهاي'),('Asia/Tokyo',52,'Tokyo','طوكيو'),('Australia/Sydney',53,'Sydney','سيدني'),
 ('UTC',99,'UTC (no offset)','التوقيت العالمي');
INSERT INTO lookup_values (type_code, code, sort_order) SELECT 'time_zone', code, sort_order FROM tmp_seed;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'en', s.name_en FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'time_zone' AND v.code = s.code;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'ar', s.name_ar FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'time_zone' AND v.code = s.code;
DROP TEMPORARY TABLE tmp_seed;
