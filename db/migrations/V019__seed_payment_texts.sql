-- V019: texts for the payment error codes. Edit them in the back office (Texts).

INSERT INTO translations (language, translation_key, value, default_value) VALUES
('en','error.BILLING_INVALID','Enter who the receipt is made out to (up to 200 characters).','Enter who the receipt is made out to (up to 200 characters).'),
('ar','error.BILLING_INVALID','أدخل اسم من صدر باسمه الإيصال (حتى 200 حرف).','أدخل اسم من صدر باسمه الإيصال (حتى 200 حرف).'),
('en','error.INVALID_PAYMENT_METHOD','Choose how the payment was received.','Choose how the payment was received.'),
('ar','error.INVALID_PAYMENT_METHOD','اختر طريقة استلام الدفعة.','اختر طريقة استلام الدفعة.'),
('en','error.PAYMENT_NOT_FOUND','That receipt doesn''t exist.','That receipt doesn''t exist.'),
('ar','error.PAYMENT_NOT_FOUND','هذا الإيصال غير موجود.','هذا الإيصال غير موجود.');
