-- V018: fees and manual payments. No payment gateway: staff record the money they received and the site issues a numbered receipt.

-- A gap-free counter. The row is locked while a receipt is issued, so concurrent payments get consecutive numbers and a rolled-back payment never uses one.
CREATE TABLE billing_counters (
  name       VARCHAR(30) NOT NULL,
  next_value BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
INSERT INTO billing_counters (name, next_value) VALUES ('receipt', 1);

-- One receipt per paid batch. The money fields are a snapshot taken at the moment of payment and are never edited; who the receipt is made out to
-- can be corrected (audited). A receipt is never deleted: cancelling the batch voids it and its number stays on record.
CREATE TABLE payments (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  seq              BIGINT UNSIGNED NOT NULL,
  receipt_number   VARCHAR(40)  NOT NULL,
  kind             ENUM('ad','premium') NOT NULL,
  ad_batch_id      BIGINT UNSIGNED NULL,
  premium_batch_id BIGINT UNSIGNED NULL,
  description      VARCHAR(400) NOT NULL,
  quantity         INT NOT NULL,
  unit_price       DECIMAL(10,2) NOT NULL,
  subtotal         DECIMAL(12,2) NOT NULL,
  vat_percent      DECIMAL(5,2)  NOT NULL,
  vat_amount       DECIMAL(12,2) NOT NULL,
  total            DECIMAL(12,2) NOT NULL,
  currency         CHAR(3) NOT NULL DEFAULT 'USD',
  billed_to        VARCHAR(200) NOT NULL,
  billed_details   VARCHAR(500) NULL,
  method           VARCHAR(30)  NOT NULL,
  reference        VARCHAR(100) NULL,
  received_on      DATE NOT NULL,
  notes            VARCHAR(500) NULL,
  status           ENUM('issued','void') NOT NULL DEFAULT 'issued',
  void_reason      VARCHAR(200) NULL,
  voided_at        DATETIME(3) NULL,
  issued_by        BIGINT UNSIGNED NULL,
  issued_at        DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (id),
  UNIQUE KEY uq_payments_seq (seq),
  UNIQUE KEY uq_payments_number (receipt_number),
  KEY ix_payments_ad (ad_batch_id),
  KEY ix_payments_premium (premium_batch_id),
  KEY ix_payments_issued (issued_at),
  CONSTRAINT ck_payments_one_batch CHECK ((ad_batch_id IS NULL) <> (premium_batch_id IS NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Billing.VatPercent',     'Billing', 'decimal', '0',    '0',    0, 100, 0, 'VAT added to every receipt, in percent (0 = none). The rate is recorded on each receipt when it is issued.'),
 ('Billing.ReceiptPrefix',  'Billing', 'string',  'SAY-', 'SAY-', NULL, NULL, 0, 'Printed before the receipt number (for example SAY-000042). Receipts already issued keep their number.'),
 ('Billing.ReceiptDigits',  'Billing', 'int',     '6',    '6',    3, 10, 0, 'How many digits the receipt number has (padded with zeros).'),
 ('Billing.CompanyName',    'Billing', 'string',  'SaySerné', 'SaySerné', NULL, NULL, 0, 'The name printed at the top of receipts.'),
 ('Billing.CompanyAddress', 'Billing', 'string',  '',     '',     NULL, NULL, 0, 'The address printed on receipts (leave empty for none).'),
 ('Billing.CompanyTaxId',   'Billing', 'string',  '',     '',     NULL, NULL, 0, 'Your tax or registration number, printed on receipts (leave empty for none).'),
 ('Billing.ReceiptFooter',  'Billing', 'string',  'Thank you for your business.', 'Thank you for your business.', NULL, NULL, 0, 'A line printed at the bottom of receipts.');
INSERT INTO permissions (code, group_name, description) VALUES
 ('payments.read',   'Payments', 'See receipts and payment totals.'),
 ('payments.manage', 'Payments', 'Correct who a receipt is made out to.');

INSERT INTO lookup_types (code, name, description, is_system) VALUES ('payment_method', 'Payment methods', 'How a payment was received (recorded on the receipt).', 1);
CREATE TEMPORARY TABLE tmp_seed (code VARCHAR(100), sort_order INT, name_en VARCHAR(200), name_ar VARCHAR(200));
INSERT INTO tmp_seed VALUES
 ('cash',          1, 'Cash',                               'نقدًا'),
 ('bank_transfer', 2, 'Bank transfer',                      'تحويل مصرفي'),
 ('cheque',        3, 'Cheque',                             'شيك'),
 ('card_external', 4, 'Card (taken outside the site)',      'بطاقة (خارج الموقع)'),
 ('other',         5, 'Other',                              'أخرى');
INSERT INTO lookup_values (type_code, code, sort_order) SELECT 'payment_method', code, sort_order FROM tmp_seed;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'en', s.name_en FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'payment_method' AND v.code = s.code;
INSERT INTO lookup_translations (value_id, language, name) SELECT v.id, 'ar', s.name_ar FROM tmp_seed s JOIN lookup_values v ON v.type_code = 'payment_method' AND v.code = s.code;
DROP TEMPORARY TABLE tmp_seed;
