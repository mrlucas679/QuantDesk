import duckdb
from loguru import logger

from quantdesk_research.resource_governor import get_resource_governor


class DuckDBCatalog:
    def __init__(self, db_path: str = ":memory:"):
        self.governor = get_resource_governor()
        self.db = duckdb.connect(db_path)
        self._apply_resource_limits()

    def _apply_resource_limits(self):
        config = self.governor.get_duckdb_config()
        self.db.execute(f"SET threads TO {config['threads']}")
        self.db.execute(f"SET memory_limit = '{config['memory_limit']}'")
        logger.info(
            f"DuckDB limits applied: threads={config['threads']}, memory={config['memory_limit']}"
        )

    def query(self, sql: str, **params):
        return self.db.execute(sql, params).pl()

    def register_parquet(self, table_name: str, path: str):
        self.db.execute(
            f"CREATE OR REPLACE VIEW {table_name} AS SELECT * FROM read_parquet(?)", [path]
        )
