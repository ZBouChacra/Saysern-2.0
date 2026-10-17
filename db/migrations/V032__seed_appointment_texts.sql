-- V032: texts for the appointment reminder and expiry e-mails. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','email.booking_reminder.subject','Reminder: your appointment on {{when}}','Reminder: your appointment on {{when}}'),
('ar','email.booking_reminder.subject','تذكير: موعدك في {{when}}','تذكير: موعدك في {{when}}'),
('en','email.booking_reminder.body','Hello {{name}},

This is a reminder of your appointment with {{other}} on {{when}}.

Details and messages:
{{link}}','Hello {{name}},

This is a reminder of your appointment with {{other}} on {{when}}.

Details and messages:
{{link}}'),
('ar','email.booking_reminder.body','مرحبًا {{name}}،

هذا تذكير بموعدك مع {{other}} في {{when}}.

التفاصيل والرسائل:
{{link}}','مرحبًا {{name}}،

هذا تذكير بموعدك مع {{other}} في {{when}}.

التفاصيل والرسائل:
{{link}}'),
('en','email.booking_expired.subject','Your appointment request was not answered','Your appointment request was not answered'),
('ar','email.booking_expired.subject','لم يتم الرد على طلب موعدك','لم يتم الرد على طلب موعدك'),
('en','email.booking_expired.body','Hello {{name}},

Your request for an appointment with {{professional}} on {{when}} was not answered in time, so it has been cancelled.

You can choose another time here:
{{link}}','Hello {{name}},

Your request for an appointment with {{professional}} on {{when}} was not answered in time, so it has been cancelled.

You can choose another time here:
{{link}}'),
('ar','email.booking_expired.body','مرحبًا {{name}}،

لم يتم الرد في الوقت المناسب على طلب موعدك مع {{professional}} في {{when}}، لذلك أُلغي.

يمكنك اختيار وقت آخر هنا:
{{link}}','مرحبًا {{name}}،

لم يتم الرد في الوقت المناسب على طلب موعدك مع {{professional}} في {{when}}، لذلك أُلغي.

يمكنك اختيار وقت آخر هنا:
{{link}}');
