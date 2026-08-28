from dataclasses import dataclass


@dataclass(frozen=True)
class LabelInterval:
    start_ns: int
    end_ns: int
