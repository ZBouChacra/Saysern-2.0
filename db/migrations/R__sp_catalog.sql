-- Repeatable: read access to the catalog (management comes with the back office in phase 3).

DROP PROCEDURE IF EXISTS sp_catalog_categories_list;
DROP PROCEDURE IF EXISTS sp_catalog_services_list;

DELIMITER $$

CREATE PROCEDURE sp_catalog_categories_list(IN p_only_active TINYINT(1))
BEGIN
  SELECT id, name, name_ar, description, description_ar, icon, is_blocked
  FROM categories
  WHERE IFNULL(p_only_active, 0) = 0 OR is_blocked = 0
  ORDER BY name;
END$$

-- p_category_id NULL = every category.
CREATE PROCEDURE sp_catalog_services_list(IN p_category_id BIGINT UNSIGNED, IN p_only_active TINYINT(1))
BEGIN
  SELECT id, category_id, name, name_ar, description, description_ar, is_blocked
  FROM services
  WHERE (p_category_id IS NULL OR category_id = p_category_id)
    AND (IFNULL(p_only_active, 0) = 0 OR is_blocked = 0)
  ORDER BY name;
END$$

DELIMITER ;
