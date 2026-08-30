import sys

from loguru import logger

from quantdesk_research.config import get_logging_config


def setup_logging() -> None:
    config = get_logging_config()

    logger.remove()

    logger.add(sys.stderr, level=config.level, format=config.format)

    if config.file_path:
        config.file_path.parent.mkdir(parents=True, exist_ok=True)
        logger.add(
            str(config.file_path),
            level=config.level,
            format=config.format,
            rotation=config.rotation,
            retention=config.retention,
        )


# Initialize on import
setup_logging()
