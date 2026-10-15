-- V004: starter catalog so a fresh install is usable. PLACEHOLDER DATA: the back office (phase 3) edits these;
-- replace with the real list from the original system before go-live.
INSERT INTO categories (id, name, name_ar, icon) VALUES
  (1, 'Legal', 'قانوني', 'scale'),
  (2, 'Health & Wellness', 'الصحة والعافية', 'heart-pulse'),
  (3, 'Engineering', 'هندسة', 'hard-hat'),
  (4, 'Design', 'تصميم', 'palette'),
  (5, 'Home Services', 'خدمات منزلية', 'wrench'),
  (6, 'Education', 'تعليم', 'book-open'),
  (7, 'Technology', 'تكنولوجيا', 'laptop');

INSERT INTO services (category_id, name, name_ar) VALUES
  (1, 'Corporate Law', 'قانون الشركات'), (1, 'Family Law', 'قانون الأسرة'), (1, 'Real Estate Law', 'قانون العقارات'),
  (2, 'Physiotherapy', 'علاج فيزيائي'), (2, 'Nutrition', 'تغذية'),
  (3, 'Civil Engineering', 'هندسة مدنية'), (3, 'Architecture', 'هندسة معمارية'),
  (4, 'Interior Design', 'تصميم داخلي'), (4, 'Graphic Design', 'تصميم غرافيكي'),
  (5, 'Electrician', 'كهربائي'), (5, 'Plumbing', 'سباكة'),
  (6, 'Math Tutoring', 'دروس رياضيات'), (6, 'Language Lessons', 'دروس لغات'),
  (7, 'Web Development', 'تطوير مواقع'), (7, 'IT Support', 'دعم تقني');
