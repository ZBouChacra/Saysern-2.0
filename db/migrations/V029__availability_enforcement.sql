-- V029: a booking must fall inside the professional's working hours (when they have set any). Switchable.
INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Booking.EnforceAvailability', 'Booking', 'bool', 'true', 'true', NULL, NULL, 1,
  'Refuse bookings outside the weekly hours a professional has set (judged in the professional''s own time zone). A professional who has set no hours is treated as always open.');
