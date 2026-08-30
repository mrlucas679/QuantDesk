import numpy as np

from quantdesk_research.experiments.crypto_direction import (
    FEATURE_NAMES,
    annual_periods,
    build_feature_frame,
    build_frame,
    chronological_slices,
    non_overlapping_returns,
    rolling_outer_slices,
    run_rolling_contrarian_baseline,
    run_rolling_cross_asset_lead_experiment,
    run_rolling_low_vol_persistence_experiment,
)


def test_annual_periods_matches_the_continuous_crypto_timeframe():
    assert annual_periods("5Min") == 365 * 24 * 12
    assert annual_periods("1Day") == 365


def test_features_do_not_change_when_future_bar_changes():
    bars = [
        {"t": f"2026-01-{1 + i // 24:02d}T{i % 24:02d}:00:00Z", "o": 100 + i,
         "h": 101 + i, "l": 99 + i, "c": 100.5 + i, "v": 10 + i, "n": 1, "vw": 100.4 + i}
        for i in range(80)
    ]
    original = build_frame(bars, horizon_bars=3)
    bars[-1]["c"] = 10_000
    changed = build_frame(bars, horizon_bars=3)

    assert original.loc[10, FEATURE_NAMES].equals(changed.loc[10, FEATURE_NAMES])


def test_actionable_feature_frame_retains_the_latest_unlabelled_decision_row():
    bars = [
        {"t": f"2026-01-{1 + i // 24:02d}T{i % 24:02d}:00:00Z", "o": 100 + i,
         "h": 101 + i, "l": 99 + i, "c": 100.5 + i, "v": 10 + i, "n": 1, "vw": 100.4 + i}
        for i in range(80)
    ]

    actionable = build_feature_frame(bars, horizon_bars=3)
    labelled = build_frame(bars, horizon_bars=3)

    assert actionable.iloc[-1]["t"] == bars[-1]["t"]
    assert len(actionable) == len(labelled) + 3


def test_chronological_slices_have_purged_boundaries():
    train, calibration, test = chronological_slices(10_000, purge_rows=3)

    assert train.stop + 3 == calibration.start
    assert calibration.stop + 3 == test.start


def test_selected_trade_returns_do_not_overlap():
    selected = non_overlapping_returns(
        np.array([1.0, 1.0, 1.0, 1.0, 1.0]),
        np.array([0.1, 0.2, 0.3, 0.4, 0.5]),
        threshold=0.5,
        horizon_bars=3,
    )

    assert selected.tolist() == [0.1, 0.4]


def test_rolling_test_windows_exclude_targets_that_cross_their_boundary():
    first, second = rolling_outer_slices(10_000, horizon_bars=3)

    assert first[0].stop + 3 == first[1].start
    assert first[1].stop + 3 == first[2].start
    assert first[2].stop + 3 == second[2].start


def test_low_volatility_experiment_rejects_a_missing_manifest(tmp_path):
    with np.testing.assert_raises(FileNotFoundError):
        run_rolling_low_vol_persistence_experiment(
            tmp_path, 60.0, 12, "missing-manifest.json", "btc-low-vol"
        )


def test_contrarian_experiment_rejects_a_missing_manifest(tmp_path):
    with np.testing.assert_raises(FileNotFoundError):
        run_rolling_contrarian_baseline(
            tmp_path, 60.0, 12, "missing-manifest.json", "btc-contrarian"
        )


def test_cross_asset_experiment_rejects_a_missing_btc_manifest(tmp_path):
    with np.testing.assert_raises(FileNotFoundError):
        run_rolling_cross_asset_lead_experiment(
            tmp_path, 60.0, 12, "missing-btc.json", "missing-eth.json", "btc-eth-lead"
        )
