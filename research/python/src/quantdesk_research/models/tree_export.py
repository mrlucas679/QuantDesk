"""Exporting a LightGBM ensemble so a hand-written traversal in C# scores it identically.

Why the trees belong inside the artifact
----------------------------------------
Scoring a gradient-boosted ensemble is not a search: walk each tree from root to leaf comparing one
feature against one threshold, sum the leaves. Two correct implementations agree to the last bit --
measured here at one unit in the last place across three boosting rounds. Fitting is a search, and
none of that belongs near a trading path.

The trees are the model, so they travel inside the artifact and inside its hash. Passing them
alongside it, as this bridge first did, produces an artifact that hashes everything except the part
which decides the answer.

Missing values are the part that was wrong
------------------------------------------
The first C# traversal sent every non-finite feature down the node's default branch. LightGBM does
not work that way, and the branch it actually takes depends on the node's ``missing_type``:

* ``"None"`` -- the model was fitted without missing values. A NaN is converted to zero and
  compared against the threshold like any other number. It does *not* take the default branch.
* ``"NaN"`` -- a NaN is missing and takes the default branch.
* ``"Zero"`` -- values within ``kZeroThreshold`` of zero are missing, as is a NaN, and take the
  default branch. Note this makes an ordinary 0.0 a missing value, and that the bound is a *float*
  literal rather than a double -- see ``ZERO_THRESHOLD``.

Measured on a fitted booster with ``missing_type = "None"``: routing a NaN down the default branch
gave 2.369 where the booster gave 5.070, and treating it as zero reproduced the booster exactly.
The wrong rule does not throw and does not look wrong; it silently scores a different leaf.

What this exporter refuses
--------------------------
Anything the C# traversal cannot reproduce exactly, which is a longer list than it looks:
categorical splits (LightGBM encodes those as bitset membership, not a threshold), linear-tree
leaves (a regression rather than a constant), ``average_output`` (random-forest mode divides rather
than sums), multiclass or multi-output boosters, and any objective carrying a link -- the sum of
leaves is on the link's scale, so a plausible number lands on the wrong one.

Early stopping is pinned rather than trusted. ``dump_model`` and ``predict`` both default to
``best_iteration``, so they agree by default -- but only by default. The iteration count is
resolved once here and passed explicitly to both, so Python cannot validate 73 rounds while C#
scores 1000.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from datetime import datetime
from typing import Any

import lightgbm as lgb
import numpy as np
from numpy.typing import NDArray

from quantdesk_research.models.runtime_artifact import (
    FeatureSemantics,
    ParityCase,
    ParitySuite,
    ProducerIdentity,
    RuntimeInferenceArtifact,
    feature_schema_of,
    utc_now,
)

#: Objectives whose raw ensemble output needs no link applied after summing.
SUPPORTED_OBJECTIVES = frozenset({"regression", "regression_l2", "l2", "mean_squared_error", "mse"})

#: Missing-value conventions the C# traversal reproduces.
SUPPORTED_MISSING_TYPES = frozenset({"None", "NaN", "Zero"})

#: The only split comparison this bridge reproduces. "==" is a categorical bitset test.
SUPPORTED_DECISION_TYPE = "<="

#: LightGBM's near-zero bound for the "Zero" missing convention.
#:
#: The literal in LightGBM is ``1e-35f`` -- a *float*, which widens to 1.0000000180025095e-35 rather
#: than to the double 1e-35. Writing the double here is wrong for exactly the values between the
#: two, and that is not a hypothetical: a fitted booster produced a split at
#: -1.0000000180025095e-35 and routed it as missing while a double 1e-35 bound called it an ordinary
#: value, sending it down the other branch. One probe in 121 disagreed, and only because the probe
#: grid feeds each tree's own thresholds back in.
ZERO_THRESHOLD = float(np.float32(1e-35))


class TreeExportRejected(Exception):
    """The booster is a shape the runtime traversal cannot reproduce exactly."""


@dataclass(frozen=True)
class FlattenedTree:
    """One tree as parallel node records, with node 0 the root.

    Flat rather than nested because the runtime walks it by index, and a nested structure crossing
    the boundary would have to be rebuilt into exactly this on arrival anyway.
    """

    nodes: list[dict[str, Any]]


def _flatten(structure: dict[str, Any]) -> FlattenedTree:
    """Turn LightGBM's nested tree into indexed nodes, refusing every unsupported split."""
    nodes: list[dict[str, Any]] = []

    def visit(node: dict[str, Any]) -> int:
        index = len(nodes)
        nodes.append({})

        if "leaf_value" in node:
            nodes[index] = {
                "split_feature": -1,
                "threshold": 0.0,
                "decision_type": SUPPORTED_DECISION_TYPE,
                "missing_type": "None",
                "default_left": True,
                "left": -1,
                "right": -1,
                "leaf_value": float(node["leaf_value"]),
            }
            return index

        decision_type = str(node.get("decision_type", ""))
        if decision_type != SUPPORTED_DECISION_TYPE:
            raise TreeExportRejected(
                f"decision_type {decision_type!r} is not a numeric threshold split"
            )
        missing_type = str(node.get("missing_type", "None"))
        if missing_type not in SUPPORTED_MISSING_TYPES:
            raise TreeExportRejected(f"missing_type {missing_type!r} is not reproduced")

        left = visit(dict(node["left_child"]))
        right = visit(dict(node["right_child"]))
        nodes[index] = {
            "split_feature": int(node["split_feature"]),
            "threshold": float(node["threshold"]),
            "decision_type": decision_type,
            "missing_type": missing_type,
            "default_left": bool(node["default_left"]),
            "left": left,
            "right": right,
            "leaf_value": 0.0,
        }
        return index

    visit(structure)
    return FlattenedTree(nodes=nodes)


def route(node: dict[str, Any], value: float) -> bool:
    """Whether ``value`` goes left at ``node``, by LightGBM's rules rather than a simpler guess.

    This is the reference the C# traversal is written against, and the ordering matters: whether a
    value counts as missing is decided by ``missing_type`` *before* any comparison happens, and only
    then does an ordinary value meet the threshold.
    """
    missing_type = node["missing_type"]
    is_nan = math.isnan(value)

    if missing_type == "NaN":
        if is_nan:
            return bool(node["default_left"])
    elif missing_type == "Zero":
        if is_nan or abs(value) <= ZERO_THRESHOLD:
            return bool(node["default_left"])
    elif is_nan:
        # missing_type "None": the model never saw a missing value, so a NaN is not routed to a
        # default branch -- LightGBM converts it to zero and compares it like anything else.
        value = 0.0

    return value <= float(node["threshold"])


def _score(trees: list[FlattenedTree], features: NDArray[np.float64]) -> float:
    """Reference traversal, used only to check the export against the booster before sealing."""
    total = 0.0
    for tree in trees:
        index = 0
        for _ in range(len(tree.nodes) + 1):
            node = tree.nodes[index]
            if node["split_feature"] < 0:
                total += float(node["leaf_value"])
                break
            index = node["left"] if route(node, float(features[node["split_feature"]])) else node["right"]
        else:  # pragma: no cover - a cycle would have to be constructed deliberately
            raise TreeExportRejected("tree traversal did not terminate")
    return total


@dataclass(frozen=True)
class TreeEnsembleExport:
    """A booster reduced to what the runtime needs, with the iteration count pinned."""

    trees: list[FlattenedTree]
    feature_names: list[str]
    feature_count: int
    objective: str
    num_iteration: int


def flatten_booster(booster: lgb.Booster) -> TreeEnsembleExport:
    """Reduce a booster to indexed trees, refusing every shape C# cannot reproduce."""
    # Resolve the iteration count once, then use the same number everywhere. best_iteration is 0
    # when early stopping never fired, which LightGBM reads as "all of them".
    num_iteration = max(0, int(booster.best_iteration))
    dump = booster.dump_model(num_iteration=num_iteration if num_iteration > 0 else None)

    objective = str(dump.get("objective", "")).split()[0]
    if objective not in SUPPORTED_OBJECTIVES:
        raise TreeExportRejected(f"objective {objective!r} carries a link this bridge does not apply")
    if bool(dump.get("average_output", False)):
        raise TreeExportRejected("average_output divides rather than sums; this is not a GBDT sum")
    if int(dump.get("num_class", 1)) != 1 or int(dump.get("num_tree_per_iteration", 1)) != 1:
        raise TreeExportRejected("multi-output boosters are not reproduced")

    trees: list[FlattenedTree] = []
    for info in dump["tree_info"]:
        if int(info.get("num_cat", 0)) != 0:
            raise TreeExportRejected("categorical splits are bitset membership, not a threshold")
        if "linear_tree" in info or "leaf_coeff" in str(info.get("tree_structure", {})):
            raise TreeExportRejected("linear-tree leaves hold a regression, not a constant")
        trees.append(_flatten(dict(info["tree_structure"])))

    names = [str(name) for name in dump["feature_names"]]
    return TreeEnsembleExport(
        trees=trees,
        feature_names=names,
        feature_count=len(names),
        objective=objective,
        num_iteration=num_iteration if num_iteration > 0 else len(trees),
    )


def tree_parity_cases(
    export: TreeEnsembleExport, booster: lgb.Booster, probes: NDArray[np.float64]
) -> list[ParityCase]:
    """Probe inputs, with the raw score ``Booster.predict`` gives for each.

    The expected value comes from the booster, never from ``_score``. That local traversal is used
    only to fail the export early if it disagrees -- if this module cannot reproduce the library it
    has no business asking C# to.
    """
    matrix = np.asarray(probes, dtype=np.float64)
    if matrix.ndim != 2 or matrix.shape[1] != export.feature_count:
        raise TreeExportRejected("probe width does not match the booster's feature count")

    predicted = np.asarray(
        booster.predict(matrix, raw_score=True, num_iteration=export.num_iteration),
        dtype=np.float64,
    )

    cases: list[ParityCase] = []
    for row, expected in zip(matrix, predicted, strict=True):
        local = _score(export.trees, row)
        if not np.isclose(local, float(expected), rtol=1e-12, atol=1e-15):
            raise TreeExportRejected(
                f"the exported trees score {local} where the booster scores {expected}; "
                "the flattening or the missing-value routing is wrong"
            )
        # A missing feature leaves as null. NaN is not a JSON literal, and a fixture carrying the
        # bare token is one .NET refuses to parse -- which matters here more than anywhere, because
        # the probe grid puts a missing value into every feature on purpose.
        cases.append(
            ParityCase(
                inputs=[[None if math.isnan(value) else float(value) for value in row]],
                expected=[float(expected)],
            )
        )
    return cases


def threshold_probes(export: TreeEnsembleExport, ordinary: NDArray[np.float64]) -> NDArray[np.float64]:
    """Probes that land on the boundaries where a wrong port is otherwise invisible.

    A traversal that resolves ``<=`` as ``<`` differs only for inputs sitting exactly on a
    threshold, which no randomly drawn probe ever does. So the thresholds are read out of the trees
    and fed back in, together with the value just below and just above, plus NaN and zero for the
    missing-value conventions.
    """
    rows: list[list[float]] = [[float(value) for value in row] for row in np.atleast_2d(ordinary)]
    base = rows[0]

    for tree in export.trees:
        for node in tree.nodes:
            feature = int(node["split_feature"])
            if feature < 0:
                continue
            threshold = float(node["threshold"])
            for value in (
                threshold,
                np.nextafter(threshold, -np.inf),
                np.nextafter(threshold, np.inf),
            ):
                probe = list(base)
                probe[feature] = float(value)
                rows.append(probe)

    # Two fillers, and neither is redundant. All-NaN separates the "None" convention -- under which
    # a NaN becomes zero and meets the threshold -- from the two that route it to a default branch.
    # It cannot separate "NaN" from "Zero", because a NaN is missing under both; those differ only
    # on values at zero, which the all-zero rows are for.
    for filler in (float("nan"), 0.0):
        rows.append([filler] * export.feature_count)
        for feature in range(export.feature_count):
            for special in (float("nan"), 0.0):
                probe = list(base)
                probe[feature] = special
                rows.append(probe)

        for tree in export.trees:
            for node in tree.nodes:
                feature = int(node["split_feature"])
                if feature < 0:
                    continue
                for value in (float(node["threshold"]), 0.0, float("nan")):
                    probe = [filler] * export.feature_count
                    probe[feature] = value
                    rows.append(probe)

    rows.extend(_probes_reaching_each_split(export))
    return np.asarray(rows, dtype=np.float64)


def _probes_reaching_each_split(export: TreeEnsembleExport) -> list[list[float]]:
    """Inputs constructed to arrive at each split, carrying a value that makes its convention matter.

    Filling every feature with one value only exercises the nodes that filling happens to reach, and
    that is not enough. On a fitted booster, three of thirty-five splits routed a zero differently
    under the "NaN" and "Zero" conventions and no probe reached any of them, so the suite looked
    thorough and separated nothing.

    Each split is therefore targeted directly. The constraints along its root-to-node path give a
    vector that lands on it -- the threshold exactly for a left branch, the next representable value
    above for a right one. The target's own feature is set from the discriminating value and never
    from the path: constraining it first and overwriting it after is what made an earlier attempt
    miss every node splitting on a feature its own path also constrains, which was all three of the
    ones that mattered. Whether the result still arrives is then decided by walking it.
    """
    probes: list[list[float]] = []

    for tree in export.trees:
        for target, node in enumerate(tree.nodes):
            if node["split_feature"] < 0:
                continue

            path = _path_to(tree, target)
            if path is None:
                continue

            split = int(node["split_feature"])
            for filler in (0.0, float("nan")):
                for value in (0.0, float("nan")):
                    candidate = [filler] * export.feature_count
                    for index, went_left in path:
                        ancestor = tree.nodes[index]
                        if int(ancestor["split_feature"]) == split:
                            continue
                        threshold = float(ancestor["threshold"])
                        candidate[int(ancestor["split_feature"])] = (
                            threshold if went_left else float(np.nextafter(threshold, np.inf))
                        )
                    candidate[split] = value

                    if _reaches(tree, candidate, target):
                        probes.append(candidate)

    return probes


def _path_to(tree: FlattenedTree, target: int) -> list[tuple[int, bool]] | None:
    """The branch taken at each ancestor of ``target``, or None when it is unreachable."""

    def walk(index: int, taken: list[tuple[int, bool]]) -> list[tuple[int, bool]] | None:
        if index == target:
            return taken
        node = tree.nodes[index]
        if node["split_feature"] < 0:
            return None
        return walk(node["left"], [*taken, (index, True)]) or walk(
            node["right"], [*taken, (index, False)]
        )

    return walk(0, [])


def _reaches(tree: FlattenedTree, features: list[float], target: int) -> bool:
    index = 0
    for _ in range(len(tree.nodes) + 1):
        if index == target:
            return True
        node = tree.nodes[index]
        if node["split_feature"] < 0:
            return False
        index = node["left"] if route(node, features[node["split_feature"]]) else node["right"]
    return False


def missing_convention_separation(
    export: TreeEnsembleExport, probes: NDArray[np.float64]
) -> dict[str, int]:
    """How many probes score differently under each missing convention this model does not use.

    Parity is only evidence if it can fail, and the traversal being verified had exactly one defect:
    it routed every missing value down the default branch, which is right for one of the three
    conventions and wrong for the other two. A probe set scoring identically under all three proves
    nothing about the thing most likely to be wrong.

    A zero here does not always mean the probes are weak. A model's structure can make the
    conventions genuinely indistinguishable: on one fitted booster every split where they disagree
    sat behind a default branch that steered missing values away from it, and forty thousand random
    inputs separated nothing because nothing could. So this reports rather than judges, and the
    caller decides what a zero means -- a fixture whose job is to catch a routing bug must separate;
    a production model has the structure it has.
    """
    conventions = {
        node["missing_type"]
        for tree in export.trees
        for node in tree.nodes
        if node["split_feature"] >= 0
    }
    baseline = np.array([_score(export.trees, row) for row in probes])

    separation: dict[str, int] = {}
    for alternative in sorted(SUPPORTED_MISSING_TYPES - conventions):
        swapped = [
            FlattenedTree(
                nodes=[
                    {**item, "missing_type": alternative} if item["split_feature"] >= 0 else item
                    for item in tree.nodes
                ]
            )
            for tree in export.trees
        ]
        scored = np.array([_score(swapped, row) for row in probes])
        separation[alternative] = int(np.sum(np.abs(scored - baseline) > 1e-12))

    return separation


def export_tree_artifact(
    booster: lgb.Booster,
    *,
    probes: NDArray[np.float64],
    artifact_id: str,
    model_id: str,
    model_version: str,
    dataset_hash: str,
    git_commit: str,
    random_seed: int,
    as_of: datetime,
    bar_duration_minutes: int,
    lookback_periods: int,
    feature_units: dict[str, str],
    target_units: str,
    evidence_grade: str = "B",
    promotion_state: str = "VALIDATED",
    require_missing_discrimination: bool = False,
) -> RuntimeInferenceArtifact:
    """Seal a booster into the artifact the runtime loads, trees and all.

    ``require_missing_discrimination`` refuses a model whose probes cannot separate the missing
    conventions. Off by default, because a production model's structure may make them genuinely
    equivalent and that is not a fault. On for the committed fixtures, whose whole job is to fail
    against a traversal that routes missing values wrongly.
    """
    export = flatten_booster(booster)
    probe_grid = threshold_probes(export, probes)
    separation = missing_convention_separation(export, probe_grid)
    if require_missing_discrimination and any(count == 0 for count in separation.values()):
        raise TreeExportRejected(
            f"probe separation by convention is {separation}; a suite scoring the same under a "
            "convention this model does not use would accept a traversal that routes missing values "
            "wrongly, which is the defect it exists to catch"
        )
    cases = tree_parity_cases(export, booster, probe_grid)

    schema = feature_schema_of(
        schema_version="lightgbm-regression-v1",
        feature_names=export.feature_names,
        dtypes=dict.fromkeys(export.feature_names, "float64"),
        normalization={},
        lookback_periods=lookback_periods,
        source_requirements=["alpaca_ohlcv"],
    )

    artifact = RuntimeInferenceArtifact(
        artifact_id=artifact_id,
        model_id=model_id,
        model_family="lightgbm",
        model_version=model_version,
        producer=ProducerIdentity(
            library="lightgbm", library_version=lgb.__version__, numpy_version=np.__version__
        ),
        feature_schema=schema,
        feature_schema_hash=schema.feature_hash,
        feature_semantics=FeatureSemantics(
            units={**feature_units, "__target__": target_units},
            # Not "refuse": LightGBM has a defined answer for a missing feature and the node
            # records which. Refusing here would discard information the model actually holds.
            missing_policy="per_node_missing_type",
            lookback_periods=lookback_periods,
            bar_duration_minutes=bar_duration_minutes,
        ),
        dataset_hash=dataset_hash,
        parameters={
            "tree_count": float(len(export.trees)),
            "feature_count": float(export.feature_count),
            "num_iteration": float(export.num_iteration),
        },
        variant={
            "objective": export.objective,
            "has_categorical_splits": "false",
            "linear_tree": "false",
            "average_output": "false",
            "zero_threshold": repr(ZERO_THRESHOLD),
        },
        payload={"trees": [tree.nodes for tree in export.trees]},
        random_seed=random_seed,
        evidence_grade=evidence_grade,
        promotion_state=promotion_state,
        diagnostics={
            "tree_count": len(export.trees),
            "num_iteration": export.num_iteration,
            "best_iteration": int(booster.best_iteration),
            "parity_probe_count": len(cases),
            "missing_convention_separation": separation,
        },
        git_commit=git_commit,
        created_at=utc_now(),
        as_of=as_of,
        parity=ParitySuite(
            kind="vector_to_scalar",
            absolute_tolerance=1e-12,
            relative_tolerance=1e-12,
            cases=cases,
        ),
    )
    return artifact.sealed()
