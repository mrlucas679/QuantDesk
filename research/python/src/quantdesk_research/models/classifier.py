import lightgbm as lgb


def train_direction_classifier(
    X_train,
    y_train,
    X_valid,
    y_valid,
    seed: int,
):
    model = lgb.LGBMClassifier(
        objective="multiclass",
        num_class=3,
        random_state=seed,
        n_estimators=500,
        learning_rate=0.03,
        num_leaves=31,
    )

    model.fit(
        X_train,
        y_train,
        eval_set=[(X_valid, y_valid)],
        callbacks=[lgb.early_stopping(50)],
    )

    return model
