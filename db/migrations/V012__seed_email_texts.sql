-- V012: texts for the e-mails the site sends and the e-mail related errors. Edit them in the back office (Texts). {{placeholders}} are filled in when the e-mail is sent.

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','email.verify_email.subject','Confirm your email address on {{site}}','Confirm your email address on {{site}}'),
('ar','email.verify_email.subject','أكّد بريدك الإلكتروني على {{site}}','أكّد بريدك الإلكتروني على {{site}}'),
('en','email.verify_email.body','Hello {{name}},

Please confirm your email address by opening this link:
{{link}}

The link is valid for {{hours}} hours. If you did not create an account, you can ignore this email.','Hello {{name}},

Please confirm your email address by opening this link:
{{link}}

The link is valid for {{hours}} hours. If you did not create an account, you can ignore this email.'),
('ar','email.verify_email.body','مرحبًا {{name}}،

يرجى تأكيد بريدك الإلكتروني بفتح هذا الرابط:
{{link}}

الرابط صالح لمدة {{hours}} ساعة. إذا لم تقم بإنشاء حساب، يمكنك تجاهل هذه الرسالة.','مرحبًا {{name}}،

يرجى تأكيد بريدك الإلكتروني بفتح هذا الرابط:
{{link}}

الرابط صالح لمدة {{hours}} ساعة. إذا لم تقم بإنشاء حساب، يمكنك تجاهل هذه الرسالة.'),
('en','email.password_reset.subject','Reset your {{site}} password','Reset your {{site}} password'),
('ar','email.password_reset.subject','إعادة تعيين كلمة مرور {{site}}','إعادة تعيين كلمة مرور {{site}}'),
('en','email.password_reset.body','Hello {{name}},

We received a request to reset your password. Open this link to choose a new one:
{{link}}

The link is valid for {{minutes}} minutes and works once. If you did not ask for this, ignore this email: your password stays the same.','Hello {{name}},

We received a request to reset your password. Open this link to choose a new one:
{{link}}

The link is valid for {{minutes}} minutes and works once. If you did not ask for this, ignore this email: your password stays the same.'),
('ar','email.password_reset.body','مرحبًا {{name}}،

تلقينا طلبًا لإعادة تعيين كلمة مرورك. افتح هذا الرابط لاختيار كلمة مرور جديدة:
{{link}}

الرابط صالح لمدة {{minutes}} دقيقة ويعمل مرة واحدة. إذا لم تطلب ذلك، تجاهل هذه الرسالة وستبقى كلمة مرورك كما هي.','مرحبًا {{name}}،

تلقينا طلبًا لإعادة تعيين كلمة مرورك. افتح هذا الرابط لاختيار كلمة مرور جديدة:
{{link}}

الرابط صالح لمدة {{minutes}} دقيقة ويعمل مرة واحدة. إذا لم تطلب ذلك، تجاهل هذه الرسالة وستبقى كلمة مرورك كما هي.'),
('en','email.booking_requested.subject','New appointment request from {{client}}','New appointment request from {{client}}'),
('ar','email.booking_requested.subject','طلب موعد جديد من {{client}}','طلب موعد جديد من {{client}}'),
('en','email.booking_requested.body','Hello {{name}},

{{client}} requested an appointment on {{when}}.

Note from the client: {{note}}

Review and answer it here:
{{link}}','Hello {{name}},

{{client}} requested an appointment on {{when}}.

Note from the client: {{note}}

Review and answer it here:
{{link}}'),
('ar','email.booking_requested.body','مرحبًا {{name}}،

طلب {{client}} موعدًا بتاريخ {{when}}.

ملاحظة من العميل: {{note}}

راجع الطلب وأجب عليه هنا:
{{link}}','مرحبًا {{name}}،

طلب {{client}} موعدًا بتاريخ {{when}}.

ملاحظة من العميل: {{note}}

راجع الطلب وأجب عليه هنا:
{{link}}'),
('en','email.booking_approved.subject','Your appointment with {{professional}} is confirmed','Your appointment with {{professional}} is confirmed'),
('ar','email.booking_approved.subject','تم تأكيد موعدك مع {{professional}}','تم تأكيد موعدك مع {{professional}}'),
('en','email.booking_approved.body','Hello {{name}},

{{professional}} confirmed your appointment on {{when}}.

See the details here:
{{link}}','Hello {{name}},

{{professional}} confirmed your appointment on {{when}}.

See the details here:
{{link}}'),
('ar','email.booking_approved.body','مرحبًا {{name}}،

أكّد {{professional}} موعدك بتاريخ {{when}}.

اطّلع على التفاصيل هنا:
{{link}}','مرحبًا {{name}}،

أكّد {{professional}} موعدك بتاريخ {{when}}.

اطّلع على التفاصيل هنا:
{{link}}'),
('en','email.booking_cancelled.subject','An appointment was cancelled','An appointment was cancelled'),
('ar','email.booking_cancelled.subject','تم إلغاء موعد','تم إلغاء موعد'),
('en','email.booking_cancelled.body','Hello {{name}},

The appointment on {{when}} with {{other}} was cancelled.

Your appointments:
{{link}}','Hello {{name}},

The appointment on {{when}} with {{other}} was cancelled.

Your appointments:
{{link}}'),
('ar','email.booking_cancelled.body','مرحبًا {{name}}،

تم إلغاء الموعد بتاريخ {{when}} مع {{other}}.

مواعيدك:
{{link}}','مرحبًا {{name}}،

تم إلغاء الموعد بتاريخ {{when}} مع {{other}}.

مواعيدك:
{{link}}'),
('en','email.booking_completed.subject','How was your appointment with {{professional}}?','How was your appointment with {{professional}}?'),
('ar','email.booking_completed.subject','كيف كان موعدك مع {{professional}}؟','كيف كان موعدك مع {{professional}}؟'),
('en','email.booking_completed.body','Hello {{name}},

Your appointment with {{professional}} is complete. Tell others how it went:
{{link}}','Hello {{name}},

Your appointment with {{professional}} is complete. Tell others how it went:
{{link}}'),
('ar','email.booking_completed.body','مرحبًا {{name}}،

اكتمل موعدك مع {{professional}}. شاركنا رأيك ليستفيد الآخرون:
{{link}}','مرحبًا {{name}}،

اكتمل موعدك مع {{professional}}. شاركنا رأيك ليستفيد الآخرون:
{{link}}'),
('en','email.new_message.subject','New message from {{sender}}','New message from {{sender}}'),
('ar','email.new_message.subject','رسالة جديدة من {{sender}}','رسالة جديدة من {{sender}}'),
('en','email.new_message.body','Hello {{name}},

{{sender}} sent you a message. Read it and reply here:
{{link}}','Hello {{name}},

{{sender}} sent you a message. Read it and reply here:
{{link}}'),
('ar','email.new_message.body','مرحبًا {{name}}،

أرسل لك {{sender}} رسالة. اقرأها وأجب عليها هنا:
{{link}}','مرحبًا {{name}}،

أرسل لك {{sender}} رسالة. اقرأها وأجب عليها هنا:
{{link}}'),
('en','email.review_received.subject','{{reviewer}} reviewed you: {{rating}}/5','{{reviewer}} reviewed you: {{rating}}/5'),
('ar','email.review_received.subject','قيّمك {{reviewer}}: {{rating}}/5','قيّمك {{reviewer}}: {{rating}}/5'),
('en','email.review_received.body','Hello {{name}},

{{reviewer}} left you a {{rating}}-star review.

See it here:
{{link}}','Hello {{name}},

{{reviewer}} left you a {{rating}}-star review.

See it here:
{{link}}'),
('ar','email.review_received.body','مرحبًا {{name}}،

ترك لك {{reviewer}} تقييمًا من {{rating}} نجوم.

اطّلع عليه هنا:
{{link}}','مرحبًا {{name}}،

ترك لك {{reviewer}} تقييمًا من {{rating}} نجوم.

اطّلع عليه هنا:
{{link}}'),
('en','email.alert_digest.subject','New professionals for your saved search "{{alert}}"','New professionals for your saved search "{{alert}}"'),
('ar','email.alert_digest.subject','محترفون جدد لبحثك المحفوظ "{{alert}}"','محترفون جدد لبحثك المحفوظ "{{alert}}"'),
('en','email.alert_digest.body','Hello {{name}},

{{count}} new professionals match your saved search "{{alert}}":

{{list}}

See them all:
{{link}}','Hello {{name}},

{{count}} new professionals match your saved search "{{alert}}":

{{list}}

See them all:
{{link}}'),
('ar','email.alert_digest.body','مرحبًا {{name}}،

يوجد {{count}} محترفين جدد يطابقون بحثك المحفوظ "{{alert}}":

{{list}}

شاهدهم جميعًا:
{{link}}','مرحبًا {{name}}،

يوجد {{count}} محترفين جدد يطابقون بحثك المحفوظ "{{alert}}":

{{list}}

شاهدهم جميعًا:
{{link}}'),
('en','email.test.subject','Test email from {{site}}','Test email from {{site}}'),
('ar','email.test.subject','رسالة تجريبية من {{site}}','رسالة تجريبية من {{site}}'),
('en','email.test.body','This is a test message from {{site}}. If you can read it, your email settings work.','This is a test message from {{site}}. If you can read it, your email settings work.'),
('ar','email.test.body','هذه رسالة تجريبية من {{site}}. إذا كنت تقرأها فإن إعدادات البريد تعمل.','هذه رسالة تجريبية من {{site}}. إذا كنت تقرأها فإن إعدادات البريد تعمل.'),
('en','email.signoff','— The {{site}} team','— The {{site}} team'),
('ar','email.signoff','— فريق {{site}}','— فريق {{site}}'),
('en','email.footer','You receive this email because you have an account on {{site}}. You can choose which emails you get here: {{settings_link}}','You receive this email because you have an account on {{site}}. You can choose which emails you get here: {{settings_link}}'),
('ar','email.footer','تصلك هذه الرسالة لأن لديك حسابًا على {{site}}. يمكنك اختيار الرسائل التي تصلك من هنا: {{settings_link}}','تصلك هذه الرسالة لأن لديك حسابًا على {{site}}. يمكنك اختيار الرسائل التي تصلك من هنا: {{settings_link}}'),
('en','error.EMAIL_NOT_VERIFIED','Please confirm your email address first. We sent you a link; you can ask for a new one.','Please confirm your email address first. We sent you a link; you can ask for a new one.'),
('ar','error.EMAIL_NOT_VERIFIED','يرجى تأكيد بريدك الإلكتروني أولًا. أرسلنا لك رابطًا ويمكنك طلب رابط جديد.','يرجى تأكيد بريدك الإلكتروني أولًا. أرسلنا لك رابطًا ويمكنك طلب رابط جديد.'),
('en','error.TOO_MANY_EMAILS','We already sent you several emails. Please wait a while before asking again.','We already sent you several emails. Please wait a while before asking again.'),
('ar','error.TOO_MANY_EMAILS','أرسلنا لك عدة رسائل بالفعل. يرجى الانتظار قليلًا قبل الطلب مجددًا.','أرسلنا لك عدة رسائل بالفعل. يرجى الانتظار قليلًا قبل الطلب مجددًا.'),
('en','error.INVALID_TOKEN','This link is invalid or has expired. Please ask for a new one.','This link is invalid or has expired. Please ask for a new one.'),
('ar','error.INVALID_TOKEN','هذا الرابط غير صالح أو انتهت صلاحيته. يرجى طلب رابط جديد.','هذا الرابط غير صالح أو انتهت صلاحيته. يرجى طلب رابط جديد.'),
('en','error.NOTIFICATION_NOT_ALLOWED','That notification cannot be changed.','That notification cannot be changed.'),
('ar','error.NOTIFICATION_NOT_ALLOWED','لا يمكن تغيير هذا الإشعار.','لا يمكن تغيير هذا الإشعار.'),
('en','error.EMAIL_NOT_CONFIGURED','Email is not switched on. Choose the SMTP provider and fill in the mail server first.','Email is not switched on. Choose the SMTP provider and fill in the mail server first.'),
('ar','error.EMAIL_NOT_CONFIGURED','البريد الإلكتروني غير مُفعّل. اختر مزوّد SMTP وأدخل بيانات خادم البريد أولًا.','البريد الإلكتروني غير مُفعّل. اختر مزوّد SMTP وأدخل بيانات خادم البريد أولًا.'),
('en','error.EMAIL_SEND_FAILED','The mail server could not send the message.','The mail server could not send the message.'),
('ar','error.EMAIL_SEND_FAILED','تعذّر على خادم البريد إرسال الرسالة.','تعذّر على خادم البريد إرسال الرسالة.');
