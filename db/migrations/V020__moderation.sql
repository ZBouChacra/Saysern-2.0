-- V020: review replies and moderation, chat deletion and staff moderation.

-- A professional may answer a review (and edit the answer). Staff may hide a review (soft delete: nothing is erased, and it can be restored).
ALTER TABLE reviews
  ADD COLUMN reply_text       TEXT NULL,
  ADD COLUMN replied_at       DATETIME(3) NULL,
  ADD COLUMN reply_edited_at  DATETIME(3) NULL,
  ADD COLUMN is_deleted       TINYINT(1) NOT NULL DEFAULT 0,
  ADD COLUMN deleted_at       DATETIME(3) NULL,
  ADD COLUMN deleted_by       BIGINT UNSIGNED NULL,
  ADD COLUMN delete_reason    VARCHAR(300) NULL,
  ADD KEY ix_reviews_deleted (is_deleted, created_at);

-- A deleted message keeps its content for staff but is hidden from the two people in the conversation. A message written by staff is flagged.
ALTER TABLE chat_messages
  ADD COLUMN is_staff   TINYINT(1) NOT NULL DEFAULT 0,
  ADD COLUMN deleted_at DATETIME(3) NULL,
  ADD COLUMN deleted_by BIGINT UNSIGNED NULL;

-- Deleting a conversation clears it for that person only (the other keeps theirs). cleared_up_to is the last message they no longer see;
-- a newer message makes the conversation reappear showing only what came after. Staff still see everything, and who deleted what.
CREATE TABLE chat_room_users (
  room_id       BIGINT UNSIGNED NOT NULL,
  user_id       BIGINT UNSIGNED NOT NULL,
  deleted_at    DATETIME(3) NULL,
  cleared_up_to BIGINT UNSIGNED NOT NULL DEFAULT 0,
  PRIMARY KEY (room_id, user_id),
  CONSTRAINT fk_chat_room_users_room FOREIGN KEY (room_id) REFERENCES chat_rooms (id) ON DELETE CASCADE,
  CONSTRAINT fk_chat_room_users_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO app_settings (setting_key, group_name, value_type, value_text, default_text, min_value, max_value, is_public, description) VALUES
 ('Reviews.MaxReplyLength', 'Reviews', 'int', '1000', '1000', 20, 5000, 1, 'The longest answer a professional may write to a review.');
INSERT INTO permissions (code, group_name, description) VALUES
 ('reviews.read',   'Moderation', 'See all reviews, including hidden ones.'),
 ('reviews.manage', 'Moderation', 'Hide and restore reviews.'),
 ('chats.read',     'Moderation', 'Read any conversation (every reading is recorded in the audit log).'),
 ('chats.post',     'Moderation', 'Write in a conversation as the SaySerné team (highlighted, audited, e-mailed to both people).');

INSERT INTO lookup_values (type_code, code, sort_order, is_active, metadata) VALUES
 ('notification_event', 'review_reply',  10, 1, NULL),
 ('notification_event', 'staff_message', 11, 1, '{"mandatory":true}');
INSERT INTO lookup_translations (value_id, language, name)
SELECT v.id, t.language, t.name FROM lookup_values v JOIN (
  SELECT 'review_reply' code, 'en' language, 'A professional answers my review' name UNION ALL SELECT 'review_reply', 'ar', 'رد المحترف على تقييمي'
  UNION ALL SELECT 'staff_message', 'en', 'A message from the SaySerné team' UNION ALL SELECT 'staff_message', 'ar', 'رسالة من فريق سيّرنه') t
  ON t.code = v.code WHERE v.type_code = 'notification_event';
