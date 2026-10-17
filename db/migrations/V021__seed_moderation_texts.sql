-- V021: texts for review replies, staff messages and moderation errors. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','email.review_reply.subject','{{professional}} replied to your review','{{professional}} replied to your review'),
('ar','email.review_reply.subject','ردّ {{professional}} على تقييمك','ردّ {{professional}} على تقييمك'),
('en','email.review_reply.body','Hello {{name}},

{{professional}} answered the review you wrote. Read the reply here:
{{link}}','Hello {{name}},

{{professional}} answered the review you wrote. Read the reply here:
{{link}}'),
('ar','email.review_reply.body','مرحبًا {{name}}،

ردّ {{professional}} على التقييم الذي كتبته. اقرأ الرد هنا:
{{link}}','مرحبًا {{name}}،

ردّ {{professional}} على التقييم الذي كتبته. اقرأ الرد هنا:
{{link}}'),
('en','email.staff_message.subject','A message from the {{site}} team','A message from the {{site}} team'),
('ar','email.staff_message.subject','رسالة من فريق {{site}}','رسالة من فريق {{site}}'),
('en','email.staff_message.body','Hello {{name}},

The {{site}} team wrote in your conversation with {{other}}. Read it here:
{{link}}','Hello {{name}},

The {{site}} team wrote in your conversation with {{other}}. Read it here:
{{link}}'),
('ar','email.staff_message.body','مرحبًا {{name}}،

كتب فريق {{site}} في محادثتك مع {{other}}. اقرأ الرسالة هنا:
{{link}}','مرحبًا {{name}}،

كتب فريق {{site}} في محادثتك مع {{other}}. اقرأ الرسالة هنا:
{{link}}'),
('en','error.REVIEW_NOT_FOUND','That review doesn''t exist.','That review doesn''t exist.'),
('ar','error.REVIEW_NOT_FOUND','هذا التقييم غير موجود.','هذا التقييم غير موجود.'),
('en','error.INVALID_REPLY','Write a reply (it must not be empty or too long).','Write a reply (it must not be empty or too long).'),
('ar','error.INVALID_REPLY','اكتب ردًا (يجب ألا يكون فارغًا أو طويلًا جدًا).','اكتب ردًا (يجب ألا يكون فارغًا أو طويلًا جدًا).'),
('en','error.MESSAGE_NOT_FOUND','That message doesn''t exist, or you can''t delete it.','That message doesn''t exist, or you can''t delete it.'),
('ar','error.MESSAGE_NOT_FOUND','هذه الرسالة غير موجودة أو لا يمكنك حذفها.','هذه الرسالة غير موجودة أو لا يمكنك حذفها.'),
('en','error.REASON_REQUIRED','Please give a short reason (it is kept in the audit log).','Please give a short reason (it is kept in the audit log).'),
('ar','error.REASON_REQUIRED','يرجى كتابة سبب مختصر (يُحفظ في سجل التدقيق).','يرجى كتابة سبب مختصر (يُحفظ في سجل التدقيق).');
