-- V002: profile fields carried over from the original app (photos are URLs; image upload comes later).
ALTER TABLE users
  ADD COLUMN phone              VARCHAR(50)   NULL,
  ADD COLUMN sex                ENUM('male','female') NULL,
  ADD COLUMN date_of_birth      DATE          NULL,
  ADD COLUMN nationality        VARCHAR(100)  NULL,
  ADD COLUMN country            VARCHAR(100)  NULL,
  ADD COLUMN city               VARCHAR(100)  NULL,
  ADD COLUMN profile_photo      VARCHAR(500)  NULL,
  ADD COLUMN banner_photo       VARCHAR(500)  NULL,
  ADD COLUMN bio                TEXT          NULL,
  ADD COLUMN portfolio          TEXT          NULL,
  ADD COLUMN preferred_language ENUM('en','ar') NOT NULL DEFAULT 'en',
  ADD COLUMN is_premium         TINYINT(1)    NOT NULL DEFAULT 0,
  ADD COLUMN is_starred         TINYINT(1)    NOT NULL DEFAULT 0;
