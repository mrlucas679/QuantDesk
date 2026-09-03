"""The artifact a fitted model crosses the language boundary in.

Why this exists as a second contract
------------------------------------
``ModelArtifact`` describes a *strategy*: which family it belongs to, what exit policy it owns,
and the R-gate evidence that licenses it to trade. This describes a *fitted model*: the numbers an
inference path needs, and the proof that a reimplementation of that path agrees with the library
which produced them. They are not the same lifecycle. A strategy can be licensed to trade with no
fitted model behind it, and a fitted model can exist with no strategy yet entitled to use it.
Merging them would make every fitted model drag R-gate evidence it does not have, and the
promotion ladder would stop meaning anything.

What was wrong before
---------------------
The runtime could refuse a model it could not reproduce, but nothing produced an artifact for it
to refuse, and the parity vectors in the C# tests were computed by the C# implementation itself.
That is a check of the code against its own arithmetic. It proves nothing about the library that
fitted the model, which is the only thing parity was ever supposed to establish.

So the rule here is narrow and absolute: **every expected output in a parity case is obtained by
calling the fitting library's own public prediction API.** Not by this module's understanding of
what the library does, and not by re-deriving the answer from the exported parameters. If the
exporter cannot get an answer out of the library itself, the model does not get an artifact.

That rule is what makes the bridge worth anything, and it is easy to violate by accident -- see
the HMM exporter, where the obvious library call returns a different quantity than the runtime
computes and happens to agree with it at exactly the row a spot-check would look at.
"""

from __future__ import annotations

import hashlib
import json
import math
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Final, Literal

from pydantic import BaseModel, Field, field_validator, model_validator

from quantdesk_research.contracts.feature_schema import FeatureSchema

ARTIFACT_SCHEMA_VERSION: Final = "runtime-inference-v2"

ModelFamily = Literal["har", "garch", "hmm", "lightgbm"]

ParityKind = Literal["vector_to_scalar", "sequence_to_vector"]

DECISION_CAPABLE_STATES = frozenset({"VALIDATED", "SHADOW", "EXPLORATION", "EXPLOITATION"})


class ProducerIdentity(BaseModel):
    """Which library, at which version, produced the numbers.

    Recorded because inference is reproduced by hand in another language. When a port and a library
    disagree the first question is always which version of the library, and an artifact that cannot
    answer it is an artifact whose parity failure cannot be diagnosed.
    """

    library: str
    library_version: str
    numpy_version: str

    @field_validator("library", "library_version", "numpy_version")
    @classmethod
    def must_be_present(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("producer identity fields must be non-empty")
        return value


class ParityCase(BaseModel):
    """One input the fitting library scored, and the answer it gave.

    ``inputs`` is a sequence of observations even for a stateless model, where it is a sequence of
    one. That keeps a single shape for both kinds rather than two structures that drift apart, and
    it makes the stateful case -- where the answer depends on everything before it -- the default
    rather than the exception.

    A missing feature is ``null``, never a floating-point NaN. JSON has no NaN literal: Python will
    happily write the bare token ``NaN`` and read it back, but it is not JSON, and .NET's parser
    rejects it outright. A fixture carrying one is a file the runtime cannot open -- which is
    exactly what the first generated LightGBM fixture was, since its probe grid deliberately
    includes a missing value for every feature.
    """

    inputs: list[list[float | None]]
    expected: list[float]

    @model_validator(mode="after")
    def shapes_are_usable(self) -> ParityCase:
        if not self.inputs or not all(self.inputs):
            raise ValueError("a parity case needs at least one non-empty observation")
        width = len(self.inputs[0])
        if any(len(row) != width for row in self.inputs):
            raise ValueError("parity observations must all have the same width")
        for row in self.inputs:
            for value in row:
                if value is not None and not math.isfinite(value):
                    raise ValueError("a missing or infinite feature must be null, not a float token")
        if not self.expected:
            raise ValueError("a parity case needs an expected output")
        if any(not math.isfinite(value) for value in self.expected):
            raise ValueError("a parity expectation must be finite")
        return self


class ParitySuite(BaseModel):
    """The cases, how to interpret them, and how close counts as agreement.

    The tolerance is per family rather than global because one number cannot mean the same thing
    for a variance of 1e-8, a return in basis points and a probability bounded by one. It is set
    from the disagreement two correct implementations produce summing the same terms in a different
    order, and it sits far below any difference that would change a decision.
    """

    kind: ParityKind
    absolute_tolerance: float
    relative_tolerance: float
    cases: list[ParityCase]

    @model_validator(mode="after")
    def suite_can_actually_refuse(self) -> ParitySuite:
        if not self.cases:
            raise ValueError("a parity suite with no cases cannot refuse anything")
        if self.absolute_tolerance < 0 or self.relative_tolerance < 0:
            raise ValueError("parity tolerances must be non-negative")
        if self.kind == "vector_to_scalar" and any(len(c.expected) != 1 for c in self.cases):
            raise ValueError("vector_to_scalar parity expects exactly one output per case")
        return self


class FeatureSemantics(BaseModel):
    """What the numbers in a feature vector actually are.

    A dot product can be perfectly implemented while being fed the wrong quantity. The schema hash
    catches a runtime computing a *different* feature set; it cannot catch one computing the right
    set in the wrong units, because the names and the ordering are identical and only the
    magnitudes are wrong -- which is exactly the failure that produces confident numbers nothing
    downstream can question.
    """

    units: dict[str, str]
    missing_policy: str
    lookback_periods: int
    bar_duration_minutes: int

    @field_validator("lookback_periods", "bar_duration_minutes")
    @classmethod
    def must_be_positive(cls, value: int) -> int:
        if value <= 0:
            raise ValueError("feature semantics durations must be positive")
        return value


class SupportDomain(BaseModel):
    """What this model was fitted on, and therefore what it may be asked about.

    Nothing carried this before, and the consequence was live and silent: one HAR and one GARCH,
    both fitted on the BTC/USD five-minute series, were consulted for SPY, QQQ, IWM and DIA. Every
    check passed -- the schema hashes matched, the parity cases reproduced -- because no check
    could compare what the model was fitted on against what it was being asked, the artifact having
    never said.

    A variance model carried across instruments is not a slightly worse one. Bitcoin's realised
    variance and an equity ETF's differ by roughly an order of magnitude, and their session
    structure differs completely.
    """

    asset_class: str
    symbols: list[str]
    bar_duration_minutes: int

    @field_validator("asset_class")
    @classmethod
    def asset_class_must_be_named(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("support domain must name an asset class")
        return value

    @field_validator("symbols")
    @classmethod
    def symbols_must_not_be_empty(cls, value: list[str]) -> list[str]:
        """An artifact fitted on nothing identifiable cannot have its reach bounded."""
        if not value or any(not symbol.strip() for symbol in value):
            raise ValueError("support domain must name at least one symbol")
        return value

    @field_validator("bar_duration_minutes")
    @classmethod
    def bar_must_be_positive(cls, value: int) -> int:
        """Carried separately from the feature schema hash, which covers names and ordering only.

        A HAR fitted on five-minute bars and fed one-minute bars has an identical schema hash and
        is a different model.
        """
        if value <= 0:
            raise ValueError("support domain bar duration must be positive")
        return value


class RuntimeInferenceArtifact(BaseModel):
    """Everything a reimplemented inference path needs, and everything needed to refuse it.

    ``payload`` carries family-specific structure that will not fit a flat map of floats -- the
    trees of an ensemble, most importantly. It lives inside the artifact and therefore inside the
    artifact hash, because the trees *are* the model: an ensemble whose scoring data arrives out of
    band is an artifact that hashes everything except the part which decides the answer.
    """

    artifact_schema_version: Literal["runtime-inference-v2"] = ARTIFACT_SCHEMA_VERSION
    artifact_id: str
    model_id: str
    model_family: ModelFamily
    model_version: str
    producer: ProducerIdentity

    feature_schema: FeatureSchema
    feature_schema_hash: str
    feature_semantics: FeatureSemantics
    support_domain: SupportDomain
    dataset_hash: str

    parameters: dict[str, float]
    variant: dict[str, str]
    payload: dict[str, Any] = Field(default_factory=dict)

    random_seed: int
    evidence_grade: str
    promotion_state: str
    diagnostics: dict[str, Any]

    git_commit: str
    created_at: datetime
    as_of: datetime

    parity: ParitySuite
    artifact_hash: str = ""

    @model_validator(mode="after")
    def artifact_is_self_consistent(self) -> RuntimeInferenceArtifact:
        if self.feature_schema_hash != self.feature_schema.feature_hash:
            raise ValueError("artifact feature_schema_hash does not match its own schema")
        # The bar appears twice and has to agree. feature_semantics states what the features were
        # computed on; support_domain states what may be asked. An artifact whose two answers
        # differ cannot be applied correctly under either reading.
        if (
            self.support_domain.bar_duration_minutes
            != self.feature_semantics.bar_duration_minutes
        ):
            raise ValueError(
                "support_domain bar duration disagrees with feature_semantics bar duration"
            )
        if self.promotion_state not in DECISION_CAPABLE_STATES:
            raise ValueError(f"promotion_state must be one of {sorted(DECISION_CAPABLE_STATES)}")
        for name, value in self.parameters.items():
            if not math.isfinite(value):
                raise ValueError(f"parameter {name} is not finite")
        width = len(self.feature_schema.feature_names)
        for case in self.parity.cases:
            if len(case.inputs[0]) != width:
                raise ValueError("parity case width does not match the feature schema")
        return self

    def sealed(self) -> RuntimeInferenceArtifact:
        """The artifact with its hash filled in over every other field.

        Sealing last and over everything is the point. A hash covering only the manifest would
        leave the trees, the variant flags and the parity answers free to change without changing
        the identity, and those are precisely the fields whose corruption produces a model that
        still loads.
        """
        document = self.model_dump(mode="json", exclude={"artifact_hash"})
        digest = hashlib.sha256(
            json.dumps(
                document, sort_keys=True, separators=(",", ":"), allow_nan=False
            ).encode("utf-8")
        ).hexdigest()
        return self.model_copy(update={"artifact_hash": digest})

    def hash_matches(self) -> bool:
        return bool(self.artifact_hash) and self.sealed().artifact_hash == self.artifact_hash

    def write(self, path: Path) -> Path:
        """Write the sealed artifact atomically, so a reader never sees half of one."""
        sealed = self if self.hash_matches() else self.sealed()
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(
            json.dumps(
                sealed.model_dump(mode="json"), sort_keys=True, indent=1, allow_nan=False
            ),
            encoding="utf-8",
        )
        temporary.replace(path)
        return path


def feature_schema_of(
    *,
    schema_version: str,
    feature_names: list[str],
    dtypes: dict[str, str],
    normalization: dict[str, Any],
    lookback_periods: int,
    source_requirements: list[str],
) -> FeatureSchema:
    """Build a schema and its hash by the one recipe both publication paths already use.

    The recipe was duplicated in ``crypto_direction`` and ``rule_contract_publication``. Two copies
    of a hash function is one edit away from two hashes for the same schema, and the whole point of
    the hash is that a runtime computing the same features arrives at the same string.
    """
    document = {
        "schema_version": schema_version,
        "feature_names": feature_names,
        "dtypes": dtypes,
        "normalization": normalization,
        "lookback_periods": lookback_periods,
        "source_requirements": source_requirements,
    }
    feature_hash = hashlib.sha256(json.dumps(document, sort_keys=True).encode("utf-8")).hexdigest()
    return FeatureSchema(
        schema_version=schema_version,
        feature_names=feature_names,
        dtypes=dtypes,
        normalization=normalization,
        lookback_periods=lookback_periods,
        source_requirements=source_requirements,
        feature_hash=feature_hash,
    )


def utc_now() -> datetime:
    return datetime.now(UTC)
