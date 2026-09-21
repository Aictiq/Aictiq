-- Runs once, on the first init of a fresh pgdata volume (mounted into
-- /docker-entrypoint-initdb.d/ by docker-compose.yml). Paired with the
-- shared_preload_libraries setting on the postgres service: the nightly perf job
-- (perf.yml) dumps its top queries from this extension. Smoke and perf always
-- start from a fresh volume, so the extension is there when they need it; an operator
-- upgrading an existing volume runs this file once by hand.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
