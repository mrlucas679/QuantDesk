import pytest

from quantdesk_research.evaluation.ablation import AblationTester
from quantdesk_research.evaluation.robustness import RobustnessTester, TransferTester


def test_robustness_tester():
    def mock_eval(model, dataset, **kwargs):
        fee = kwargs.get("fee", 0.0)
        window = kwargs.get("window", 10)
        # Higher fee -> lower sharpe
        # Different window -> different sharpe
        return {"sharpe": 2.0 - fee - (window / 100.0)}

    tester = RobustnessTester(base_model="model", base_dataset="data", eval_func=mock_eval)

    fee_results = tester.test_fee_sensitivity([0.0, 0.01, 0.05])
    assert fee_results[0.0] == pytest.approx(1.9)  # 2.0 - 0.0 - 0.1
    assert fee_results[0.05] == pytest.approx(1.85)  # 2.0 - 0.05 - 0.1

    window_results = tester.test_window_sensitivity([10, 20, 30])
    assert window_results[10] == pytest.approx(1.9)
    assert window_results[30] == pytest.approx(1.7)


def test_transfer_tester():
    def mock_eval(model, dataset):
        return {"sharpe": 1.5 if dataset == "AAPL" else 1.2}

    def mock_loader(instrument=None, regime=None):
        return instrument or regime

    tester = TransferTester(base_model="model", eval_func=mock_eval, data_loader=mock_loader)

    assert tester.test_instrument_transfer("AAPL") == 1.5
    assert tester.test_instrument_transfer("MSFT") == 1.2
    assert tester.test_regime_transfer("HighVol") == 1.2


def test_ablation_tester():
    def mock_trainer(dataset):
        # Count features in dataset (which is just the list of features in our mock)
        return {"sharpe": len(dataset) * 0.5}

    def mock_builder(features):
        return features

    tester = AblationTester(model_trainer=mock_trainer, dataset_builder=mock_builder)

    base_features = ["f1", "f2", "f3"]
    ablation_groups = {"group1": ["f1"], "group2": ["f2", "f3"]}

    results = tester.run_ablation(base_features, ablation_groups)
    assert results["base"] == 1.5  # 3 * 0.5
    assert results["group1"] == 1.0  # 2 * 0.5
    assert results["group2"] == 0.5  # 1 * 0.5
