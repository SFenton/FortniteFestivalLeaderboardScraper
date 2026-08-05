\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    SELECT current_database() AS database_name,
           current_user AS database_user,
           current_setting('port') AS server_port,
           COALESCE(pg_catalog.inet_server_addr()::text, 'local-socket')
               AS server_address,
           pg_catalog.pg_is_in_recovery()::text AS in_recovery,
           control.system_identifier::text AS system_identifier,
           role_row.rolsuper::text AS role_superuser,
           role_row.rolbypassrls::text AS role_bypass_rls
    FROM pg_catalog.pg_control_system() control
    JOIN pg_catalog.pg_roles role_row
      ON role_row.rolname = current_user
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
