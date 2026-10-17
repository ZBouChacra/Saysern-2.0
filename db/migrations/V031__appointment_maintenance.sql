-- V031: a request nobody answered expires, and approved appointments send a reminder. Both are settings.

ALTER TABLE appointments ADD COLUMN reminder_sent_at DATETIME(3) NULL, ADD KEY ix_appointments_due (status, starts_at);

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Booking.ReminderHours', 'Booking', 'int',  '24',   '24',   0, 168, 1, 'How many hours before an approved appointment both people are reminded by e-mail (0 = no reminders).'),
 ('Booking.ExpirePending', 'Booking', 'bool', 'true', 'true', NULL, NULL, 1, 'Cancel a booking request that nobody answered by the time it was due to start (the client is told).');

INSERT INTO lookup_values (type_code, code, sort_order, is_active, metadata) VALUES
 ('notification_event', 'booking_reminder', 14, 1, NULL),
 ('notification_event', 'booking_expired',  15, 1, NULL);
INSERT INTO lookup_translations (value_id, language, name)
SELECT v.id, t.language, t.name FROM lookup_values v JOIN (
  SELECT 'booking_reminder' code, 'en' language, 'A reminder before my appointment' name UNION ALL SELECT 'booking_reminder', 'ar', 'تذكير قبل موعدي'
  UNION ALL SELECT 'booking_expired', 'en', 'My appointment request expired unanswered' UNION ALL SELECT 'booking_expired', 'ar', 'انتهت صلاحية طلب موعدي دون رد') t
  ON t.code = v.code WHERE v.type_code = 'notification_event';
