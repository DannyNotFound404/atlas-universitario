CREATE TABLE training_topics
(
    id                       UUID PRIMARY KEY,
    code                     VARCHAR(64) NOT NULL UNIQUE,
    title                    VARCHAR(180) NOT NULL,
    security_focus           TEXT NOT NULL,
    category                 VARCHAR(64) NOT NULL,
    default_duration_minutes INTEGER NOT NULL DEFAULT 30,
    default_modality         VARCHAR(24) NOT NULL DEFAULT 'VIRTUAL',
    active                   BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc           TIMESTAMPTZ NOT NULL,
    created_by               VARCHAR(64) NOT NULL,
    updated_at_utc           TIMESTAMPTZ NOT NULL,
    updated_by               VARCHAR(64) NOT NULL,
    CONSTRAINT ck_training_topics_duration
        CHECK (default_duration_minutes BETWEEN 5 AND 480),
    CONSTRAINT ck_training_topics_modality
        CHECK (default_modality IN ('VIRTUAL', 'IN_PERSON', 'HYBRID'))
);

CREATE INDEX ix_training_topics_active
    ON training_topics (active, title);

CREATE TABLE training_cycles
(
    id              UUID PRIMARY KEY,
    name            VARCHAR(120) NOT NULL UNIQUE,
    start_date      DATE NOT NULL,
    end_date        DATE NOT NULL,
    status          VARCHAR(16) NOT NULL,
    notes           TEXT,
    created_at_utc  TIMESTAMPTZ NOT NULL,
    created_by      VARCHAR(64) NOT NULL,
    updated_at_utc  TIMESTAMPTZ NOT NULL,
    updated_by      VARCHAR(64) NOT NULL,
    CONSTRAINT ck_training_cycles_status
        CHECK (status IN ('DRAFT', 'ACTIVE', 'CLOSED', 'ARCHIVED')),
    CONSTRAINT ck_training_cycles_dates
        CHECK (end_date >= start_date)
);

CREATE UNIQUE INDEX ux_training_cycles_one_active
    ON training_cycles ((status))
    WHERE status = 'ACTIVE';

CREATE INDEX ix_training_cycles_dates
    ON training_cycles (start_date DESC, end_date DESC);

CREATE TABLE training_sessions
(
    id                    UUID PRIMARY KEY,
    cycle_id              UUID NOT NULL REFERENCES training_cycles(id)
                                ON DELETE RESTRICT,
    topic_id              UUID NOT NULL REFERENCES training_topics(id)
                                ON DELETE RESTRICT,
    sequence_number       INTEGER NOT NULL,
    scheduled_date        DATE NOT NULL,
    start_time            TIME,
    title                 VARCHAR(180) NOT NULL,
    security_focus        TEXT NOT NULL,
    category              VARCHAR(64) NOT NULL,
    modality              VARCHAR(24) NOT NULL,
    duration_minutes      INTEGER NOT NULL,
    status                VARCHAR(20) NOT NULL,
    trainer               VARCHAR(160),
    material_reference    VARCHAR(500),
    presentation_url      VARCHAR(1000),
    attendance_form_url   VARCHAR(1000),
    notes                 TEXT,
    created_at_utc        TIMESTAMPTZ NOT NULL,
    created_by            VARCHAR(64) NOT NULL,
    updated_at_utc        TIMESTAMPTZ NOT NULL,
    updated_by            VARCHAR(64) NOT NULL,
    CONSTRAINT ck_training_sessions_sequence
        CHECK (sequence_number BETWEEN 1 AND 999),
    CONSTRAINT ck_training_sessions_duration
        CHECK (duration_minutes BETWEEN 5 AND 480),
    CONSTRAINT ck_training_sessions_modality
        CHECK (modality IN ('VIRTUAL', 'IN_PERSON', 'HYBRID')),
    CONSTRAINT ck_training_sessions_status
        CHECK (status IN ('DRAFT', 'PLANNED', 'COMPLETED',
                          'RESCHEDULED', 'CANCELLED'))
);

CREATE INDEX ix_training_sessions_cycle
    ON training_sessions (cycle_id, scheduled_date, sequence_number);

CREATE INDEX ix_training_sessions_status
    ON training_sessions (status, scheduled_date);

CREATE TABLE training_participants
(
    id                       UUID PRIMARY KEY,
    account_name             VARCHAR(160),
    normalized_account_name  VARCHAR(160),
    source_identity_key      CHAR(64),
    display_name             VARCHAR(180) NOT NULL,
    email                    VARCHAR(254),
    department               VARCHAR(160),
    agency_label             VARCHAR(180),
    agency_code              VARCHAR(64) REFERENCES agencies(code)
                                 ON UPDATE CASCADE ON DELETE SET NULL,
    active                   BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc           TIMESTAMPTZ NOT NULL,
    created_by               VARCHAR(64) NOT NULL,
    updated_at_utc           TIMESTAMPTZ NOT NULL,
    updated_by               VARCHAR(64) NOT NULL,
    CONSTRAINT ck_training_participants_account
        CHECK ((account_name IS NULL) = (normalized_account_name IS NULL))
);

CREATE UNIQUE INDEX ux_training_participants_account
    ON training_participants (normalized_account_name)
    WHERE normalized_account_name IS NOT NULL;

CREATE UNIQUE INDEX ux_training_participants_source_identity
    ON training_participants (source_identity_key)
    WHERE source_identity_key IS NOT NULL;

CREATE INDEX ix_training_participants_active
    ON training_participants (active, display_name);

CREATE INDEX ix_training_participants_agency
    ON training_participants (agency_code, active);

CREATE TABLE training_attendance_imports
(
    id                    UUID PRIMARY KEY,
    session_id            UUID NOT NULL REFERENCES training_sessions(id)
                              ON DELETE CASCADE,
    original_file_name    VARCHAR(255) NOT NULL,
    media_type            VARCHAR(120) NOT NULL,
    stored_relative_path  VARCHAR(500) NOT NULL UNIQUE,
    size_bytes            BIGINT NOT NULL,
    sha256                CHAR(64) NOT NULL,
    source_row_count      INTEGER NOT NULL,
    accepted_row_count    INTEGER NOT NULL,
    duplicate_row_count   INTEGER NOT NULL,
    rejected_row_count    INTEGER NOT NULL,
    header_mapping        JSONB NOT NULL,
    warnings              JSONB NOT NULL,
    is_current            BOOLEAN NOT NULL DEFAULT TRUE,
    imported_at_utc       TIMESTAMPTZ NOT NULL,
    imported_by           VARCHAR(64) NOT NULL,
    CONSTRAINT ck_training_attendance_import_size
        CHECK (size_bytes BETWEEN 1 AND 10000000),
    CONSTRAINT ck_training_attendance_import_hash
        CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_training_attendance_import_counts
        CHECK (source_row_count >= 0 AND accepted_row_count >= 0 AND
               duplicate_row_count >= 0 AND rejected_row_count >= 0 AND
               accepted_row_count + duplicate_row_count + rejected_row_count
                   <= source_row_count)
);

CREATE UNIQUE INDEX ux_training_attendance_import_current
    ON training_attendance_imports (session_id)
    WHERE is_current;

CREATE INDEX ix_training_attendance_import_session
    ON training_attendance_imports (session_id, imported_at_utc DESC);

CREATE TABLE training_session_attendance
(
    session_id         UUID NOT NULL REFERENCES training_sessions(id)
                            ON DELETE CASCADE,
    participant_id     UUID NOT NULL REFERENCES training_participants(id)
                            ON DELETE RESTRICT,
    import_id          UUID NOT NULL REFERENCES training_attendance_imports(id)
                            ON DELETE CASCADE,
    source_row_number  INTEGER NOT NULL,
    display_name       VARCHAR(180) NOT NULL,
    email              VARCHAR(254),
    agency_label       VARCHAR(180) NOT NULL,
    department         VARCHAR(160) NOT NULL,
    status             VARCHAR(16) NOT NULL,
    responded_at_utc   TIMESTAMPTZ NOT NULL,
    checked_in_at_utc  TIMESTAMPTZ,
    notes              VARCHAR(500),
    recorded_at_utc    TIMESTAMPTZ NOT NULL,
    recorded_by        VARCHAR(64) NOT NULL,
    PRIMARY KEY (session_id, participant_id),
    CONSTRAINT ux_training_attendance_import_row
        UNIQUE (import_id, source_row_number),
    CONSTRAINT ck_training_attendance_source_row
        CHECK (source_row_number >= 2),
    CONSTRAINT ck_training_attendance_status
        CHECK (status IN ('PENDING', 'PRESENT', 'ABSENT', 'EXCUSED'))
);

CREATE INDEX ix_training_attendance_participant
    ON training_session_attendance (participant_id, session_id);

CREATE INDEX ix_training_attendance_response
    ON training_session_attendance (session_id, responded_at_utc, display_name);

-- Importación inicial del calendario 2026–2027 compartido por el usuario.
INSERT INTO training_cycles
    (id, name, start_date, end_date, status, notes,
     created_at_utc, created_by, updated_at_utc, updated_by)
VALUES
    ('d40de51a-fd92-4af6-a899-904be8e96001',
     'Ciclo 2026–2027', DATE '2026-06-01', DATE '2027-05-31', 'ACTIVE',
     'Importado del Programa Anual de Capacitaciones de Ciberseguridad.',
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration');

INSERT INTO training_topics
    (id, code, title, security_focus, category,
     default_duration_minutes, default_modality, active,
     created_at_utc, created_by, updated_at_utc, updated_by)
VALUES
    ('d40de51a-fd92-4af6-a899-904be8e96101', 'INTRO_CYBERSECURITY',
     'Introducción a la ciberseguridad',
     'Comprender la importancia de la seguridad, los riesgos comunes y la responsabilidad individual.',
     'Concientización', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96102', 'PHISHING_EMAIL',
     'Phishing y correos sospechosos',
     'Identificar correos falsos, enlaces maliciosos, adjuntos peligrosos y señales de alerta.',
     'Prevención', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96103', 'SECURE_PASSWORDS',
     'Contraseñas seguras',
     'Aplicar buenas prácticas para crear, proteger y no reutilizar credenciales.',
     'Buenas prácticas', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96104', 'MALWARE_RANSOMWARE',
     'Virus, antivirus y ransomware',
     'Atender las alertas, evitar infecciones, aplicar reinicios oportunos y responder de forma segura.',
     'Prevención', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96105', 'DIGITAL_HYGIENE',
     'Higiene digital del usuario final',
     'Reducir riesgos mediante bloqueo de pantalla, manejo ordenado de archivos, escritorio limpio y navegación responsable.',
     'Buenas prácticas', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96106', 'FIRST_PERIOD_REVIEW',
     'Evaluación y refuerzo del primer período',
     'Repasar los temas vistos, resolver dudas y reforzar buenas prácticas mediante casos breves.',
     'Refuerzo', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96107', 'SOCIAL_ENGINEERING',
     'Ingeniería social',
     'Reconocer intentos de manipulación por llamadas, mensajes, urgencias falsas o solicitudes inusuales.',
     'Prevención', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96108', 'INFORMATION_PROTECTION',
     'Protección de información institucional',
     'Manejar documentos, datos sensibles y archivos institucionales con responsabilidad.',
     'Buenas prácticas', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96109', 'SAFE_BROWSING',
     'Navegación segura en internet',
     'Evitar sitios peligrosos, descargas no confiables, formularios falsos y publicidad engañosa.',
     'Prevención', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96110', 'MOBILE_SECURITY',
     'Seguridad en dispositivos móviles',
     'Proteger cuentas, códigos de verificación, mensajería, enlaces sospechosos y aplicaciones no confiables.',
     'Buenas prácticas', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96111', 'SUSPICIOUS_INCIDENT_RESPONSE',
     'Actuación ante incidentes sospechosos',
     'Saber cómo actuar ante correos, enlaces, archivos, llamadas o comportamientos extraños del equipo.',
     'Respuesta básica', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96112', 'CYCLE_CLOSURE',
     'Cierre del ciclo y cultura de ciberseguridad',
     'Reforzar aprendizajes, compartir recomendaciones finales y promover el compromiso institucional.',
     'Cierre', 30, 'VIRTUAL', TRUE,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration');

INSERT INTO training_sessions
    (id, cycle_id, topic_id, sequence_number, scheduled_date, start_time,
     title, security_focus, category, modality, duration_minutes, status,
     trainer, material_reference, notes,
     created_at_utc, created_by, updated_at_utc, updated_by)
VALUES
    ('d40de51a-fd92-4af6-a899-904be8e96201', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96101', 1, DATE '2026-06-05', NULL,
     'Introducción a la ciberseguridad', 'Comprender la importancia de la seguridad, los riesgos comunes y la responsabilidad individual.', 'Concientización', 'VIRTUAL', 30, 'PLANNED', NULL, '01_introduccion_resumen_general_ciberseguridad_sin_marcas', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96202', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96102', 2, DATE '2026-07-03', NULL,
     'Phishing y correos sospechosos', 'Identificar correos falsos, enlaces maliciosos, adjuntos peligrosos y señales de alerta.', 'Prevención', 'VIRTUAL', 30, 'COMPLETED', NULL, '02_phishing_y_correos_sospechosos', 'Importada con estado Realizada; asistencia pendiente de registrar.',
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96203', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96103', 3, DATE '2026-08-07', NULL,
     'Contraseñas seguras', 'Aplicar buenas prácticas para crear, proteger y no reutilizar credenciales.', 'Buenas prácticas', 'VIRTUAL', 30, 'PLANNED', NULL, '03_contrasenas_seguras_flexible', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96204', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96104', 4, DATE '2026-09-04', NULL,
     'Virus, antivirus y ransomware', 'Atender las alertas, evitar infecciones, aplicar reinicios oportunos y responder de forma segura.', 'Prevención', 'VIRTUAL', 30, 'PLANNED', NULL, '04_virus_comunes_antivirus_y_respuesta_segura', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96205', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96105', 5, DATE '2026-10-02', NULL,
     'Higiene digital del usuario final', 'Reducir riesgos mediante bloqueo de pantalla, manejo ordenado de archivos, escritorio limpio y navegación responsable.', 'Buenas prácticas', 'VIRTUAL', 30, 'PLANNED', NULL, '05_higiene_digital_usuario_final_higienico', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96206', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96106', 6, DATE '2026-11-06', NULL,
     'Evaluación y refuerzo del primer período', 'Repasar los temas vistos, resolver dudas y reforzar buenas prácticas mediante casos breves.', 'Refuerzo', 'VIRTUAL', 30, 'PLANNED', NULL, '06_evaluacion_y_refuerzo_primer_periodo_estilo_actual', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96207', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96107', 7, DATE '2026-12-04', NULL,
     'Ingeniería social', 'Reconocer intentos de manipulación por llamadas, mensajes, urgencias falsas o solicitudes inusuales.', 'Prevención', 'VIRTUAL', 30, 'PLANNED', NULL, 'en desarrollo', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96208', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96108', 8, DATE '2027-01-07', NULL,
     'Protección de información institucional', 'Manejar documentos, datos sensibles y archivos institucionales con responsabilidad.', 'Buenas prácticas', 'VIRTUAL', 30, 'PLANNED', NULL, 'en desarrollo', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96209', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96109', 9, DATE '2027-02-05', NULL,
     'Navegación segura en internet', 'Evitar sitios peligrosos, descargas no confiables, formularios falsos y publicidad engañosa.', 'Prevención', 'VIRTUAL', 30, 'PLANNED', NULL, 'en desarrollo', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96210', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96110', 10, DATE '2027-03-05', NULL,
     'Seguridad en dispositivos móviles', 'Proteger cuentas, códigos de verificación, mensajería, enlaces sospechosos y aplicaciones no confiables.', 'Buenas prácticas', 'VIRTUAL', 30, 'PLANNED', NULL, 'en desarrollo', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96211', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96111', 11, DATE '2027-04-02', NULL,
     'Actuación ante incidentes sospechosos', 'Saber cómo actuar ante correos, enlaces, archivos, llamadas o comportamientos extraños del equipo.', 'Respuesta básica', 'VIRTUAL', 30, 'PLANNED', NULL, 'en desarrollo', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration'),
    ('d40de51a-fd92-4af6-a899-904be8e96212', 'd40de51a-fd92-4af6-a899-904be8e96001', 'd40de51a-fd92-4af6-a899-904be8e96112', 12, DATE '2027-05-07', NULL,
     'Cierre del ciclo y cultura de ciberseguridad', 'Reforzar aprendizajes, compartir recomendaciones finales y promover el compromiso institucional.', 'Cierre', 'VIRTUAL', 30, 'PLANNED', NULL, 'en desarrollo', NULL,
     CURRENT_TIMESTAMP, 'migration', CURRENT_TIMESTAMP, 'migration');
