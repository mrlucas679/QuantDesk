from dataclasses import dataclass


@dataclass(frozen=True)
class EvalResult:
    case_id: str
    metrics: dict[str, float]
    hard_failures: tuple[str, ...]
    evidence_refs: tuple[str, ...]


@dataclass(frozen=True)
class QuantEvalCase:
    case_id: str
    hypothesis_family_id: str
    dataset_version: str
    time_frontier: str
    feature_schema_hash: str
    transform_artifact_ids: tuple[str, ...]
    split_protocol: str
    cost_model_version: str
    execution_environment_version: str
    baseline_ids: tuple[str, ...]
    seed: int


@dataclass(frozen=True)
class AgentEvalCase:
    case_id: str
    task_family: str
    evaluation_mode: str
    environment_version: str
    prompt_template_version: str
    tool_registry_version: str
    tool_permission_profile: str
    maximum_steps: int
    maximum_tokens: int
    seed: int


def evaluate_model(case, pipeline) -> EvalResult:
    dataset = pipeline.load_point_in_time(case)
    splits = pipeline.build_splits(dataset, case)
    pipeline.validate_no_leakage(splits, case)

    baselines = pipeline.run_baselines(splits, case)
    challenger = pipeline.run_challenger(splits, case)

    economic = pipeline.apply_cost_and_execution_models(
        challenger,
        case,
    )

    return pipeline.build_result(
        case,
        baselines,
        challenger,
        economic,
    )
