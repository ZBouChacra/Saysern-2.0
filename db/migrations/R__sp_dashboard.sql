-- Repeatable: the numbers on the back-office home page. One cheap row of counts; the API shows each group only to staff who may see it.

DROP PROCEDURE IF EXISTS sp_dashboard_stats;

DELIMITER $$

CREATE PROCEDURE sp_dashboard_stats()
BEGIN
  SELECT
    (SELECT COUNT(*) FROM users)                                                                                         AS users_total,
    (SELECT COUNT(*) FROM users WHERE created_at >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 7 DAY))                           AS users_7d,
    (SELECT COUNT(*) FROM users WHERE created_at >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 30 DAY))                          AS users_30d,
    (SELECT COUNT(*) FROM users WHERE profile_type = 'professional')                                                     AS professionals,
    (SELECT COUNT(*) FROM users WHERE is_locked = 1)                                                                     AS users_locked,
    (SELECT COUNT(*) FROM professions)                                                                                   AS listings,
    (SELECT COUNT(*) FROM appointments WHERE status = 'pending'   AND created_at >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 30 DAY)) AS appointments_pending,
    (SELECT COUNT(*) FROM appointments WHERE status = 'approved'  AND created_at >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 30 DAY)) AS appointments_approved,
    (SELECT COUNT(*) FROM appointments WHERE status = 'completed' AND created_at >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 30 DAY)) AS appointments_completed,
    (SELECT COUNT(*) FROM appointments WHERE status = 'cancelled' AND created_at >= DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 30 DAY)) AS appointments_cancelled,
    (SELECT COUNT(*) FROM reviews WHERE is_deleted = 0)                                                                  AS reviews_total,
    (SELECT COUNT(*) FROM reviews WHERE is_deleted = 1)                                                                  AS reviews_hidden,
    (SELECT IFNULL(ROUND(AVG(rating), 2), 0) FROM reviews WHERE is_deleted = 0)                                          AS reviews_avg,
    (SELECT COUNT(*) FROM contact_messages WHERE status = 'new')                                                         AS contact_new,
    (SELECT COUNT(*) FROM contact_messages WHERE status <> 'spam')                                                       AS contact_total,
    (SELECT COUNT(DISTINCT b.ad_id) FROM ad_batches b JOIN ads a ON a.id = b.ad_id WHERE a.is_active = 1 AND b.status = 'paid' AND UTC_DATE() BETWEEN b.date_from AND b.date_to) AS ads_running,
    (SELECT COUNT(*) FROM ad_batches WHERE status = 'pending')                                                           AS ad_batches_pending,
    (SELECT COUNT(DISTINCT profession_id) FROM premium_days WHERE day = UTC_DATE())                                      AS premium_today,
    (SELECT COUNT(*) FROM premium_batches WHERE status = 'pending')                                                      AS premium_pending,
    (SELECT COUNT(*) FROM payments WHERE status = 'issued' AND received_on >= DATE_SUB(UTC_DATE(), INTERVAL 29 DAY))     AS receipts_30d,
    (SELECT IFNULL(SUM(total), 0) FROM payments WHERE status = 'issued' AND received_on >= DATE_SUB(UTC_DATE(), INTERVAL 29 DAY)) AS received_30d,
    (SELECT IFNULL(SUM(total), 0) FROM payments WHERE status = 'issued' AND received_on >= DATE_FORMAT(UTC_DATE(), '%Y-%m-01')) AS received_month,
    (SELECT COUNT(*) FROM payments WHERE status = 'void' AND received_on >= DATE_SUB(UTC_DATE(), INTERVAL 29 DAY))      AS receipts_void_30d,
    (SELECT COUNT(*) FROM jobs WHERE status = 'failed')                                                                  AS jobs_failed,
    (SELECT COUNT(*) FROM jobs WHERE status IN ('pending', 'running'))                                                   AS jobs_pending,
    (SELECT COUNT(*) FROM email_outbox WHERE status = 'failed')                                                          AS emails_failed,
    (SELECT COUNT(*) FROM email_outbox WHERE status = 'queued')                                                          AS emails_queued;
END$$

DELIMITER ;
