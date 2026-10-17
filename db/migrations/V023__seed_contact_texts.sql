-- V023: texts for contact e-mails and errors. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','email.contact_reply.subject','Re: {{subject}}','Re: {{subject}}'),
('ar','email.contact_reply.subject','رد: {{subject}}','رد: {{subject}}'),
('en','email.contact_reply.body','Hello {{name}},

Thank you for contacting {{site}}. Here is our reply:

{{reply}}

Your message was:

{{message}}','Hello {{name}},

Thank you for contacting {{site}}. Here is our reply:

{{reply}}

Your message was:

{{message}}'),
('ar','email.contact_reply.body','مرحبًا {{name}}،

شكرًا لتواصلك مع {{site}}. هذا ردنا:

{{reply}}

رسالتك كانت:

{{message}}','مرحبًا {{name}}،

شكرًا لتواصلك مع {{site}}. هذا ردنا:

{{reply}}

رسالتك كانت:

{{message}}'),
('en','email.contact_received.subject','New contact message: {{subject}}','New contact message: {{subject}}'),
('ar','email.contact_received.subject','رسالة تواصل جديدة: {{subject}}','رسالة تواصل جديدة: {{subject}}'),
('en','email.contact_received.body','{{name}} ({{from}}) wrote to {{site}}:

{{message}}

Open it in the back office:
{{link}}','{{name}} ({{from}}) wrote to {{site}}:

{{message}}

Open it in the back office:
{{link}}'),
('ar','email.contact_received.body','كتب {{name}} ({{from}}) إلى {{site}}:

{{message}}

افتحها في لوحة الإدارة:
{{link}}','كتب {{name}} ({{from}}) إلى {{site}}:

{{message}}

افتحها في لوحة الإدارة:
{{link}}'),
('en','error.CONTACT_NOT_FOUND','That message doesn''t exist.','That message doesn''t exist.'),
('ar','error.CONTACT_NOT_FOUND','هذه الرسالة غير موجودة.','هذه الرسالة غير موجودة.'),
('en','error.CONTACT_INVALID','Please fill in your name, a valid email address, a subject and your message.','Please fill in your name, a valid email address, a subject and your message.'),
('ar','error.CONTACT_INVALID','يرجى كتابة اسمك وبريد إلكتروني صحيح وموضوع ورسالتك.','يرجى كتابة اسمك وبريد إلكتروني صحيح وموضوع ورسالتك.'),
('en','error.REPLY_REQUIRED','Write a reply first.','Write a reply first.'),
('ar','error.REPLY_REQUIRED','اكتب ردًا أولًا.','اكتب ردًا أولًا.');
