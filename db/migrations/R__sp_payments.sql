-- Repeatable: receipts. sp_payment_issue / sp_payment_void run INSIDE the caller's transaction (they never start one),
-- so a payment that fails later (for example premium without capacity) is rolled back together with its receipt number.

DROP PROCEDURE IF EXISTS sp_payment_issue;
DROP PROCEDURE IF EXISTS sp_payment_void;
DROP PROCEDURE IF EXISTS sp_payments_list;
DROP PROCEDURE IF EXISTS sp_payments_get;
DROP PROCEDURE IF EXISTS sp_payments_update_billing;
DROP PROCEDURE IF EXISTS sp_payments_summary;

DELIMITER $$

CREATE PROCEDURE sp_payment_issue(
  IN p_actor_id BIGINT UNSIGNED, IN p_kind VARCHAR(10), IN p_ad_batch_id BIGINT UNSIGNED, IN p_premium_batch_id BIGINT UNSIGNED,
  IN p_description VARCHAR(400), IN p_quantity INT, IN p_unit_price DECIMAL(10,2), IN p_subtotal DECIMAL(12,2),
  IN p_billed_to VARCHAR(200), IN p_billed_details VARCHAR(500), IN p_method VARCHAR(30), IN p_reference VARCHAR(100), IN p_received_on DATE, IN p_notes VARCHAR(500))
BEGIN
  DECLARE v_seq BIGINT UNSIGNED;
  DECLARE v_prefix VARCHAR(100);
  DECLARE v_digits INT;
  DECLARE v_vat_pct DECIMAL(5,2);
  DECLARE v_vat DECIMAL(12,2);
  DECLARE v_number VARCHAR(40);
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_received DATE;
  IF p_billed_to IS NULL OR CHAR_LENGTH(TRIM(p_billed_to)) = 0 OR CHAR_LENGTH(TRIM(p_billed_to)) > 200 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BILLING_INVALID';
  END IF;
  IF p_method IS NULL OR NOT EXISTS (SELECT 1 FROM lookup_values WHERE type_code = 'payment_method' AND code = p_method AND is_active = 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_PAYMENT_METHOD';
  END IF;
  SET v_received = IFNULL(p_received_on, UTC_DATE());
  IF v_received > UTC_DATE() THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DATES';
  END IF;
  SELECT value_text INTO v_prefix FROM app_settings WHERE setting_key = 'Billing.ReceiptPrefix';
  SELECT CAST(value_text AS SIGNED) INTO v_digits FROM app_settings WHERE setting_key = 'Billing.ReceiptDigits';
  SELECT CAST(value_text AS DECIMAL(5,2)) INTO v_vat_pct FROM app_settings WHERE setting_key = 'Billing.VatPercent';
  SELECT next_value INTO v_seq FROM billing_counters WHERE name = 'receipt' FOR UPDATE;      -- one receipt at a time: consecutive numbers
  UPDATE billing_counters SET next_value = v_seq + 1 WHERE name = 'receipt';
  SET v_number = CONCAT(v_prefix, LPAD(v_seq, v_digits, '0'));
  SET v_vat = ROUND(p_subtotal * v_vat_pct / 100, 2);
  INSERT INTO payments (seq, receipt_number, kind, ad_batch_id, premium_batch_id, description, quantity, unit_price, subtotal, vat_percent, vat_amount, total,
                        billed_to, billed_details, method, reference, received_on, notes, issued_by)
  VALUES (v_seq, v_number, p_kind, p_ad_batch_id, p_premium_batch_id, p_description, p_quantity, p_unit_price, p_subtotal, v_vat_pct, v_vat, p_subtotal + v_vat,
          TRIM(p_billed_to), NULLIF(TRIM(p_billed_details), ''), p_method, NULLIF(TRIM(p_reference), ''), v_received, NULLIF(TRIM(p_notes), ''), p_actor_id);
  SET v_id = LAST_INSERT_ID();
  CALL sp_audit_write(p_actor_id, 'payments.issue', 'payment', v_id, CONCAT('Issued receipt ', v_number, ' to ', TRIM(p_billed_to), ' for ', p_subtotal + v_vat, ' USD (', p_method, ')'),
                      JSON_OBJECT('kind', p_kind, 'subtotal', p_subtotal, 'vat', v_vat, 'total', p_subtotal + v_vat, 'method', p_method), NULL);
END$$

-- Cancelling a paid batch voids its receipt. The number stays on record (refunds are handled outside the site).
CREATE PROCEDURE sp_payment_void(IN p_actor_id BIGINT UNSIGNED, IN p_ad_batch_id BIGINT UNSIGNED, IN p_premium_batch_id BIGINT UNSIGNED)
BEGIN
  DECLARE v_id BIGINT UNSIGNED;
  DECLARE v_number VARCHAR(40);
  SELECT id, receipt_number INTO v_id, v_number FROM payments
   WHERE status = 'issued' AND ((p_ad_batch_id IS NOT NULL AND ad_batch_id = p_ad_batch_id) OR (p_premium_batch_id IS NOT NULL AND premium_batch_id = p_premium_batch_id)) LIMIT 1;
  IF v_id IS NOT NULL THEN
    UPDATE payments SET status = 'void', void_reason = 'The booking was cancelled', voided_at = UTC_TIMESTAMP(3) WHERE id = v_id;
    CALL sp_audit_write(p_actor_id, 'payments.void', 'payment', v_id, CONCAT('Voided receipt ', v_number, ' (the booking was cancelled)'), NULL, NULL);
  END IF;
END$$

CREATE PROCEDURE sp_payments_list(IN p_status VARCHAR(10), IN p_kind VARCHAR(10), IN p_from DATE, IN p_to DATE, IN p_search VARCHAR(100), IN p_page INT, IN p_page_size INT)
BEGIN
  DECLARE v_size INT;
  DECLARE v_offset INT;
  DECLARE v_like VARCHAR(300);
  SET v_size = LEAST(GREATEST(IFNULL(p_page_size, 25), 1), 200);
  SET v_offset = (GREATEST(IFNULL(p_page, 1), 1) - 1) * v_size;
  SET v_like = IF(p_search IS NULL OR CHAR_LENGTH(TRIM(p_search)) = 0, NULL, CONCAT('%', REPLACE(REPLACE(REPLACE(TRIM(p_search), '|', '||'), '%', '|%'), '_', '|_'), '%'));
  SELECT id, receipt_number, kind, description, subtotal, vat_amount, total, currency, billed_to, method, reference, received_on, status, issued_at
  FROM payments
  WHERE (p_status IS NULL OR status = p_status) AND (p_kind IS NULL OR kind = p_kind) AND (p_from IS NULL OR received_on >= p_from) AND (p_to IS NULL OR received_on <= p_to)
    AND (v_like IS NULL OR receipt_number LIKE v_like ESCAPE '|' OR billed_to LIKE v_like ESCAPE '|' OR description LIKE v_like ESCAPE '|' OR reference LIKE v_like ESCAPE '|')
  ORDER BY seq DESC LIMIT v_size OFFSET v_offset;
  SELECT COUNT(*) AS total FROM payments
  WHERE (p_status IS NULL OR status = p_status) AND (p_kind IS NULL OR kind = p_kind) AND (p_from IS NULL OR received_on >= p_from) AND (p_to IS NULL OR received_on <= p_to)
    AND (v_like IS NULL OR receipt_number LIKE v_like ESCAPE '|' OR billed_to LIKE v_like ESCAPE '|' OR description LIKE v_like ESCAPE '|' OR reference LIKE v_like ESCAPE '|');
END$$

-- One receipt in full, with the company details printed on it (from Settings at the time of printing).
CREATE PROCEDURE sp_payments_get(IN p_id BIGINT UNSIGNED)
BEGIN
  SELECT p.id, p.seq, p.receipt_number, p.kind, p.ad_batch_id, p.premium_batch_id, p.description, p.quantity, p.unit_price, p.subtotal, p.vat_percent, p.vat_amount, p.total, p.currency,
         p.billed_to, p.billed_details, p.method, p.reference, p.received_on, p.notes, p.status, p.void_reason, p.voided_at, p.issued_at,
         (SELECT t.name FROM lookup_values v JOIN lookup_translations t ON t.value_id = v.id AND t.language = 'en' WHERE v.type_code = 'payment_method' AND v.code = p.method) AS method_name,
         (SELECT value_text FROM app_settings WHERE setting_key = 'Billing.CompanyName')    AS company_name,
         (SELECT value_text FROM app_settings WHERE setting_key = 'Billing.CompanyAddress') AS company_address,
         (SELECT value_text FROM app_settings WHERE setting_key = 'Billing.CompanyTaxId')   AS company_tax_id,
         (SELECT value_text FROM app_settings WHERE setting_key = 'Billing.ReceiptFooter')  AS footer
  FROM payments p WHERE p.id = p_id;
END$$

-- Only who the receipt is made out to (and the notes) may be corrected; the money and the number never change.
CREATE PROCEDURE sp_payments_update_billing(IN p_actor_id BIGINT UNSIGNED, IN p_id BIGINT UNSIGNED, IN p_billed_to VARCHAR(200), IN p_billed_details VARCHAR(500), IN p_notes VARCHAR(500))
BEGIN
  DECLARE v_number VARCHAR(40);
  DECLARE v_old VARCHAR(200);
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_billed_to IS NULL OR CHAR_LENGTH(TRIM(p_billed_to)) = 0 OR CHAR_LENGTH(TRIM(p_billed_to)) > 200 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BILLING_INVALID';
  END IF;
  START TRANSACTION;
  SELECT receipt_number, billed_to INTO v_number, v_old FROM payments WHERE id = p_id FOR UPDATE;
  IF v_number IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'PAYMENT_NOT_FOUND';
  END IF;
  UPDATE payments SET billed_to = TRIM(p_billed_to), billed_details = NULLIF(TRIM(p_billed_details), ''), notes = NULLIF(TRIM(p_notes), '') WHERE id = p_id;
  CALL sp_audit_write(p_actor_id, 'payments.billing', 'payment', p_id, CONCAT('Changed who receipt ', v_number, ' is made out to (was "', v_old, '")'),
                      JSON_OBJECT('old', v_old, 'new', TRIM(p_billed_to)), NULL);
  COMMIT;
  CALL sp_payments_get(p_id);
END$$

-- Money received between two dates (void receipts excluded), by kind and by method.
CREATE PROCEDURE sp_payments_summary(IN p_from DATE, IN p_to DATE)
BEGIN
  SELECT kind, method, COUNT(*) AS receipts, IFNULL(SUM(subtotal), 0) AS subtotal, IFNULL(SUM(vat_amount), 0) AS vat, IFNULL(SUM(total), 0) AS total
  FROM payments WHERE status = 'issued' AND (p_from IS NULL OR received_on >= p_from) AND (p_to IS NULL OR received_on <= p_to)
  GROUP BY kind, method ORDER BY kind, method;
  SELECT COUNT(*) AS voided FROM payments WHERE status = 'void' AND (p_from IS NULL OR received_on >= p_from) AND (p_to IS NULL OR received_on <= p_to);
END$$

DELIMITER ;
