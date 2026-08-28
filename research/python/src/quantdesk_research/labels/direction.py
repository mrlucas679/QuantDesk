def direction_label(
    future_log_return: float,
    dead_zone: float,
) -> int:
    if future_log_return > dead_zone:
        return 2  # up
    if future_log_return < -dead_zone:
        return 0  # down
    return 1  # neutral
