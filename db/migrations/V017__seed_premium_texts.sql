-- V017: texts for the premium error codes. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','error.PREMIUM_NEEDS_COUNTRY','Premium is booked per country: set this listing''s country first.','Premium is booked per country: set this listing''s country first.'),
('ar','error.PREMIUM_NEEDS_COUNTRY','يُحجز التميّز لكل بلد: حدّد بلد هذه الخدمة أولًا.','يُحجز التميّز لكل بلد: حدّد بلد هذه الخدمة أولًا.'),
('en','error.PREMIUM_OVERLAP','This listing already has a premium booking that covers some of these days.','This listing already has a premium booking that covers some of these days.'),
('ar','error.PREMIUM_OVERLAP','لدى هذه الخدمة حجز تميّز يغطي بعض هذه الأيام.','لدى هذه الخدمة حجز تميّز يغطي بعض هذه الأيام.'),
('en','error.PREMIUM_NO_CAPACITY','The country has no free premium days for this booking within the allowed time, so it cannot be marked paid.','The country has no free premium days for this booking within the allowed time, so it cannot be marked paid.'),
('ar','error.PREMIUM_NO_CAPACITY','لا توجد أيام تميّز شاغرة في هذا البلد لهذا الحجز ضمن المدة المسموحة، لذا لا يمكن اعتماد الدفع.','لا توجد أيام تميّز شاغرة في هذا البلد لهذا الحجز ضمن المدة المسموحة، لذا لا يمكن اعتماد الدفع.');
