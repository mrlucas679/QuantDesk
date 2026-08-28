import shutil

import psutil  # type: ignore[import-untyped]
from loguru import logger

from quantdesk_research.config import get_resource_config


class ResourceGovernor:
    def __init__(self):
        self.config = get_resource_config()
        self._check_initial_state()

    def _check_initial_state(self):
        ram = psutil.virtual_memory()
        total_ram_gb = ram.total / (1024**3)
        logger.info(f"System RAM: {total_ram_gb:.2f} GB")

        usage = shutil.disk_usage(".")
        free_disk_gb = usage.free / (1024**3)
        logger.info(f"Free Disk: {free_disk_gb:.2f} GB")

        if free_disk_gb < self.config.min_free_disk_gb:
            logger.warning(
                f"Low disk space: {free_disk_gb:.2f} GB < {self.config.min_free_disk_gb} GB"
            )

    def get_duckdb_config(self):
        return {
            "threads": self.config.duckdb_threads,
            "memory_limit": f"{self.config.duckdb_memory_limit_gb}GB",
        }

    def get_worker_count(self):
        return self.config.parallel_workers

    def check_limits(self):
        ram = psutil.virtual_memory()
        available_ram_gb = ram.available / (1024**3)

        if available_ram_gb < 1.0:
            logger.warning(f"Very low RAM available: {available_ram_gb:.2f} GB")
            return False
        return True


_governor = ResourceGovernor()


def get_resource_governor():
    return _governor
