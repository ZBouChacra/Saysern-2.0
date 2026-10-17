-- V026: texts for the account settings e-mails and errors. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','email.reenable_account.subject','Re-enable your {{site}} account','Re-enable your {{site}} account'),
('ar','email.reenable_account.subject','إعادة تفعيل حسابك على {{site}}','إعادة تفعيل حسابك على {{site}}'),
('en','email.reenable_account.body','Hello {{name}},

Your account is disabled. To switch it back on, confirm by opening this link:
{{link}}

The link is valid for {{hours}} hours. If you did not ask for this, ignore this email: your account stays disabled.','Hello {{name}},

Your account is disabled. To switch it back on, confirm by opening this link:
{{link}}

The link is valid for {{hours}} hours. If you did not ask for this, ignore this email: your account stays disabled.'),
('ar','email.reenable_account.body','مرحبًا {{name}}،

حسابك معطّل. لإعادة تفعيله أكّد بفتح هذا الرابط:
{{link}}

الرابط صالح لمدة {{hours}} ساعة. إذا لم تطلب ذلك فتجاهل هذه الرسالة وسيبقى حسابك معطّلًا.','مرحبًا {{name}}،

حسابك معطّل. لإعادة تفعيله أكّد بفتح هذا الرابط:
{{link}}

الرابط صالح لمدة {{hours}} ساعة. إذا لم تطلب ذلك فتجاهل هذه الرسالة وسيبقى حسابك معطّلًا.'),
('en','email.password_changed.subject','Your {{site}} password was changed','Your {{site}} password was changed'),
('ar','email.password_changed.subject','تم تغيير كلمة مرور {{site}}','تم تغيير كلمة مرور {{site}}'),
('en','email.password_changed.body','Hello {{name}},

The password of your {{site}} account was just changed, and you were signed out everywhere else. If this was you, nothing more is needed.

If it was not you, reset your password now:
{{link}}','Hello {{name}},

The password of your {{site}} account was just changed, and you were signed out everywhere else. If this was you, nothing more is needed.

If it was not you, reset your password now:
{{link}}'),
('ar','email.password_changed.body','مرحبًا {{name}}،

تم للتو تغيير كلمة مرور حسابك على {{site}} وتم تسجيل خروجك من الأجهزة الأخرى. إذا كنت أنت من فعل ذلك فلا يلزم شيء آخر.

وإن لم تكن أنت، فأعد تعيين كلمة المرور الآن:
{{link}}','مرحبًا {{name}}،

تم للتو تغيير كلمة مرور حسابك على {{site}} وتم تسجيل خروجك من الأجهزة الأخرى. إذا كنت أنت من فعل ذلك فلا يلزم شيء آخر.

وإن لم تكن أنت، فأعد تعيين كلمة المرور الآن:
{{link}}'),
('en','error.ACCOUNT_DISABLED','This account is disabled. You can switch it back on by email.','This account is disabled. You can switch it back on by email.'),
('ar','error.ACCOUNT_DISABLED','هذا الحساب معطّل. يمكنك إعادة تفعيله عبر البريد الإلكتروني.','هذا الحساب معطّل. يمكنك إعادة تفعيله عبر البريد الإلكتروني.'),
('en','error.WRONG_PASSWORD','That password is not correct.','That password is not correct.'),
('ar','error.WRONG_PASSWORD','كلمة المرور غير صحيحة.','كلمة المرور غير صحيحة.'),
('en','error.INVALID_TIME_ZONE','Choose a time zone from the list.','Choose a time zone from the list.'),
('ar','error.INVALID_TIME_ZONE','اختر منطقة زمنية من القائمة.','اختر منطقة زمنية من القائمة.');
