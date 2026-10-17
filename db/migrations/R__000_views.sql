-- Repeatable: views used by the stored procedures. Named R__000 so it is applied before the R__sp_* scripts.
-- Procedures run with their definer's rights, so the API account needs no privilege on these views.

CREATE OR REPLACE VIEW v_professional_listing AS
SELECT
  p.id AS profession_id, p.user_id, p.category_id, p.service_id,
  c.name AS category_name, c.name_ar AS category_name_ar,
  s.name AS service_name,  s.name_ar AS service_name_ar,
  p.country, p.city, p.cost_per_hour, p.years_of_experience, p.website,
  p.has_team, p.team_size, p.has_office, p.office_address, p.office_city, p.office_country,
  p.geographic_areas, p.avg_rating, p.total_reviews, p.is_hidden, p.is_locked,
  u.first_name, u.last_name, u.profile_photo, u.sex, u.nationality,
  TIMESTAMPDIFF(YEAR, u.date_of_birth, UTC_DATE()) AS age,
  EXISTS (SELECT 1 FROM premium_days pdx WHERE pdx.profession_id = p.id AND pdx.day = UTC_DATE()) AS is_premium, u.is_starred, (u.is_locked OR u.is_disabled) AS user_locked, u.profile_type
FROM professions p
JOIN users u      ON u.id = p.user_id
JOIN categories c ON c.id = p.category_id
JOIN services s   ON s.id = p.service_id;
