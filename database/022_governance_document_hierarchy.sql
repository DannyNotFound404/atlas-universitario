CREATE OR REPLACE FUNCTION enforce_governance_document_hierarchy()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $atlas$
DECLARE
    ancestor_depth INTEGER;
    creates_cycle BOOLEAN;
BEGIN
    IF NEW.document_type = 'MASTER_POLICY' AND
       NEW.parent_document_id IS NOT NULL THEN
        RAISE EXCEPTION 'La política principal debe permanecer como documento raíz.'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'ck_governance_master_policy_root';
    END IF;

    IF NEW.parent_document_id IS NULL THEN
        RETURN NEW;
    END IF;

    WITH RECURSIVE ancestors AS
    (
        SELECT id, parent_document_id, 1 AS depth, ARRAY[id] AS path
        FROM governance_documents
        WHERE id = NEW.parent_document_id
        UNION ALL
        SELECT parent.id, parent.parent_document_id,
               child.depth + 1, child.path || parent.id
        FROM governance_documents parent
        JOIN ancestors child ON parent.id = child.parent_document_id
        WHERE child.depth < 4
          AND NOT parent.id = ANY(child.path)
    )
    SELECT COALESCE(max(depth), 0),
           COALESCE(bool_or(id = NEW.id), FALSE)
    INTO ancestor_depth, creates_cycle
    FROM ancestors;

    IF ancestor_depth = 0 THEN
        RAISE EXCEPTION 'El documento superior no existe.'
            USING ERRCODE = '23503',
                  CONSTRAINT = 'fk_governance_document_parent';
    END IF;

    IF creates_cycle THEN
        RAISE EXCEPTION 'La jerarquía documental no puede contener ciclos.'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'ck_governance_document_hierarchy_cycle';
    END IF;

    IF ancestor_depth > 2 THEN
        RAISE EXCEPTION 'La jerarquía admite padre, hijo y nieto; no se permite un cuarto nivel.'
            USING ERRCODE = '23514',
                  CONSTRAINT = 'ck_governance_document_hierarchy_depth';
    END IF;

    RETURN NEW;
END;
$atlas$;

CREATE TRIGGER trg_governance_document_hierarchy
BEFORE INSERT OR UPDATE OF parent_document_id, document_type
ON governance_documents
FOR EACH ROW
EXECUTE FUNCTION enforce_governance_document_hierarchy();

DO $atlas$
DECLARE
    excessive_depth BOOLEAN;
    hierarchy_count BIGINT;
    document_count BIGINT;
BEGIN
    WITH RECURSIVE hierarchy AS
    (
        SELECT id, parent_document_id, 0 AS depth, ARRAY[id] AS path
        FROM governance_documents
        WHERE parent_document_id IS NULL
        UNION ALL
        SELECT child.id, child.parent_document_id,
               parent.depth + 1, parent.path || child.id
        FROM governance_documents child
        JOIN hierarchy parent ON child.parent_document_id = parent.id
        WHERE NOT child.id = ANY(parent.path)
    )
    SELECT EXISTS(SELECT 1 FROM hierarchy WHERE depth > 2),
           count(*)
    INTO excessive_depth, hierarchy_count
    FROM hierarchy;

    SELECT count(*)
    INTO document_count
    FROM governance_documents;

    IF excessive_depth THEN
        RAISE EXCEPTION
            'Existen documentos con más de tres niveles totales; corrija la jerarquía antes de migrar.';
    END IF;

    IF EXISTS
       (
           SELECT 1
           FROM governance_documents
           WHERE document_type = 'MASTER_POLICY'
             AND parent_document_id IS NOT NULL
       ) THEN
        RAISE EXCEPTION
            'Existe una política principal con documento superior; debe permanecer como raíz.';
    END IF;

    IF hierarchy_count <> document_count THEN
        RAISE EXCEPTION
            'La jerarquía documental existente contiene un ciclo o un padre inaccesible.';
    END IF;
END;
$atlas$;
