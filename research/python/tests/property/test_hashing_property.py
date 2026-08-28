from quantdesk_research.hashing import hash_dict


def test_feature_schema_hash_deterministic():
    schema_data = {
        "schema_version": "1.0",
        "feature_names": ["f1", "f2"],
        "dtypes": {"f1": "float64", "f2": "float64"},
        "normalization": {},
        "lookback_periods": 10,
        "source_requirements": ["price"],
        "feature_hash": "dummy",
    }

    h1 = hash_dict(schema_data)

    # Same data, different order in dict
    schema_data_reordered = {
        "feature_hash": "dummy",
        "source_requirements": ["price"],
        "lookback_periods": 10,
        "normalization": {},
        "dtypes": {"f1": "float64", "f2": "float64"},
        "feature_names": ["f1", "f2"],
        "schema_version": "1.0",
    }

    h2 = hash_dict(schema_data_reordered)

    assert h1 == h2
