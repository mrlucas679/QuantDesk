import numpy as np
from loguru import logger


def calculate_pbo(matrix_returns: np.ndarray, n_partitions: int = 16) -> float:
    """
    Calculates the Probability of Backtest Overfitting (PBO).
    matrix_returns: (T, N) array of returns for N strategies over T time periods.
    """
    T, N = matrix_returns.shape
    if N < 2:
        logger.warning("PBO requires at least 2 strategies/trials.")
        return 0.0

    # Split T into S partitions
    if T < n_partitions:
        n_partitions = T // 2

    partition_size = T // n_partitions

    # Combinations of partitions (Combinatorially Purged Cross-Validation style)
    # For simplicity, we use a jackknife/leave-one-out approach on partitions
    # if full CSCV is too expensive. Here we implement a simpler version.

    logits = []

    for i in range(n_partitions):
        # Validation set: one partition
        val_start = i * partition_size
        val_end = (i + 1) * partition_size if i < n_partitions - 1 else T

        val_idx = np.arange(val_start, val_end)
        train_idx = np.setdiff1d(np.arange(T), val_idx)

        train_returns = matrix_returns[train_idx, :]
        val_returns = matrix_returns[val_idx, :]

        # Best strategy in training
        train_sharpes = np.mean(train_returns, axis=0) / np.std(train_returns, axis=0)
        best_idx = np.argmax(train_sharpes)

        # Performance of all strategies in validation
        val_sharpes = np.mean(val_returns, axis=0) / np.std(val_returns, axis=0)

        # Rank of the "best" training strategy in validation
        rank = np.sum(val_sharpes <= val_sharpes[best_idx]) / N

        # Logit of rank
        if rank == 0:
            rank = 1e-6
        if rank == 1:
            rank = 1 - 1e-6
        logit = np.log(rank / (1 - rank))
        logits.append(logit)

    # PBO is the frequency of logits < 0 (rank < 0.5)
    pbo = np.mean(np.array(logits) < 0)
    return float(pbo)
