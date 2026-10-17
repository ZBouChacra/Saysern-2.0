-- V028: a receipt can be printed in Arabic: its line description is also stored in Arabic when the receipt is issued.
ALTER TABLE payments ADD COLUMN description_ar VARCHAR(400) NULL;
