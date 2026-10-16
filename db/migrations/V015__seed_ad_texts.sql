-- V015: texts for the advertising error codes. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','error.AD_NOT_FOUND','That ad doesn''t exist or isn''t running.','That ad doesn''t exist or isn''t running.'),
('ar','error.AD_NOT_FOUND','هذا الإعلان غير موجود أو غير فعّال.','هذا الإعلان غير موجود أو غير فعّال.'),
('en','error.AD_INACTIVE','This ad is switched off. Switch it on before reserving days.','This ad is switched off. Switch it on before reserving days.'),
('ar','error.AD_INACTIVE','هذا الإعلان متوقف. فعّله قبل حجز الأيام.','هذا الإعلان متوقف. فعّله قبل حجز الأيام.'),
('en','error.AD_CAPACITY_EXCEEDED','Too many ads are already booked for one of these days in this position and place. Choose other days.','Too many ads are already booked for one of these days in this position and place. Choose other days.'),
('ar','error.AD_CAPACITY_EXCEEDED','عدد الإعلانات المحجوزة في أحد هذه الأيام لهذا المكان والموقع تجاوز الحد. اختر أيامًا أخرى.','عدد الإعلانات المحجوزة في أحد هذه الأيام لهذا المكان والموقع تجاوز الحد. اختر أيامًا أخرى.'),
('en','error.INVALID_AD_POSITION','Choose one of the available ad positions.','Choose one of the available ad positions.'),
('ar','error.INVALID_AD_POSITION','اختر أحد أماكن الإعلان المتاحة.','اختر أحد أماكن الإعلان المتاحة.'),
('en','error.INVALID_PRICING','Enter a daily fee (zero or more) and/or a limit of at least 1.','Enter a daily fee (zero or more) and/or a limit of at least 1.'),
('ar','error.INVALID_PRICING','أدخل رسومًا يومية (صفر أو أكثر) و/أو حدًا لا يقل عن 1.','أدخل رسومًا يومية (صفر أو أكثر) و/أو حدًا لا يقل عن 1.'),
('en','error.INVALID_LINK','The link must start with http:// or https://.','The link must start with http:// or https://.'),
('ar','error.INVALID_LINK','يجب أن يبدأ الرابط بـ http:// أو https://.','يجب أن يبدأ الرابط بـ http:// أو https://.'),
('en','error.INVALID_DATES','Choose a first and last day: today or later, the last day not before the first, and at most a year.','Choose a first and last day: today or later, the last day not before the first, and at most a year.'),
('ar','error.INVALID_DATES','اختر يوم البداية والنهاية: اليوم أو لاحقًا، ولا يسبق يوم النهاية يوم البداية، وبحد أقصى سنة.','اختر يوم البداية والنهاية: اليوم أو لاحقًا، ولا يسبق يوم النهاية يوم البداية، وبحد أقصى سنة.'),
('en','error.PRICING_NOT_FOUND','That price no longer exists.','That price no longer exists.'),
('ar','error.PRICING_NOT_FOUND','هذا السعر لم يعد موجودًا.','هذا السعر لم يعد موجودًا.'),
('en','error.BATCH_NOT_FOUND','That booking no longer exists.','That booking no longer exists.'),
('ar','error.BATCH_NOT_FOUND','هذا الحجز لم يعد موجودًا.','هذا الحجز لم يعد موجودًا.'),
('en','error.INVALID_STATUS','That status is not allowed.','That status is not allowed.'),
('ar','error.INVALID_STATUS','هذه الحالة غير مسموح بها.','هذه الحالة غير مسموح بها.');
