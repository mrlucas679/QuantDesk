import lightgbm as lgb
import numpy as np
from numpy.typing import NDArray


def train_direction_classifier(
    X_train: NDArray[np.float64],
    y_train: NDArray[np.int_],
    X_valid: NDArray[np.float64],
    y_valid: NDArray[np.int_],
    seed: int,
) -> lgb.LGBMClassifier:
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
        eval_X=X_valid,
        eval_y=y_valid,
        callbacks=[lgb.early_stopping(50)],
    )

    return model
