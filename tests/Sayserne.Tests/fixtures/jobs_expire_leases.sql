UPDATE jobs SET locked_until = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 SECOND) WHERE status = 'running'
