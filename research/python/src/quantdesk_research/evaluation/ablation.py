from collections.abc import Callable

from loguru import logger


class AblationTester:
    """
    Tests what happens when specific feature groups or model components are removed.
    """

    def __init__(self, model_trainer: Callable, dataset_builder: Callable):
        self.model_trainer = model_trainer
        self.dataset_builder = dataset_builder

    def run_ablation(
        self, base_features: list[str], ablation_groups: dict[str, list[str]]
    ) -> dict[str, float]:
        """
        ablation_groups: { "name": [features_to_remove] }
        """
        results: dict[str, float] = {}

        # Base performance
        results["base"] = self._train_and_eval(base_features)

        for group_name, features_to_remove in ablation_groups.items():
            reduced_features = [f for f in base_features if f not in features_to_remove]
            logger.info(f"Running ablation for group: {group_name}")
            results[group_name] = self._train_and_eval(reduced_features)

        return results

    def _train_and_eval(self, features: list[str]) -> float:
        dataset = self.dataset_builder(features)
        metrics = self.model_trainer(dataset)
        return metrics.get("sharpe", metrics.get("score", 0.0))
