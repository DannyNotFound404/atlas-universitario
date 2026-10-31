-- Un dispositivo NAS puede publicar varios recursos SMB. Cada fila de
-- nas_appliances representa un recurso montable y las filas con el mismo
-- host/puerto se agrupan como un unico equipo fisico en el portal.

CREATE UNIQUE INDEX ux_nas_resource_endpoint
    ON nas_appliances
       (agency_code, LOWER(host), smb_port, LOWER(share_name))
    WHERE enabled = TRUE;

COMMENT ON COLUMN nas_appliances.share_name IS
    'Recurso SMB exacto. Un mismo host puede tener varios recursos registrados.';

COMMENT ON COLUMN devices.nas_appliance_id IS
    'Recurso SMB asignado al equipo; el nombre historico se conserva por compatibilidad.';
