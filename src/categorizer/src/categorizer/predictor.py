"""The port a predictor plugs into, and the one implementation there is today.

`Protocol` is `interface`, with one difference that matters here: it is
*structural*. A class satisfies `Predictor` by having the right members, without
naming it or importing it -- which is why the fake in `tests/` does not inherit
from anything, and why the Anthropic adapter of a later issue will not have to
either. The C# equivalent would need `: IPredictor` on the class; this needs
nothing.

`@runtime_checkable` is deliberately absent. It would let `isinstance(x,
Predictor)` compile, and it checks only that the *names* exist -- not the
signatures -- so it reports a pass for an object whose `predict` takes different
arguments. A type checker already does the real job at the call site; a runtime
check that is weaker than the static one is worse than no runtime check.
"""

from typing import Protocol

from categorizer.categories import NO_PREDICTION
from categorizer.contracts import CategorizeRequest, CategorizeResponse, Category, Source
from categorizer.rules import predict as predict_by_rules


class Predictor(Protocol):
    """One answer from one transaction.

    Returning the whole `CategorizeResponse` rather than a bare category is what
    keeps `source` honest: an implementation names itself, so a predictor cannot
    be wired up and then reported as something it is not. The alternative --
    returning `Category | None` and having the endpoint stamp the source from
    configuration -- puts the truth about which code answered in a different
    file from the code that answered.
    """

    def categorize(self, request: CategorizeRequest) -> CategorizeResponse: ...


class RulesPredictor:
    """`rules.predict` with the sentinel translated, and nothing else.

    Deliberately thin. Every decision worth arguing about -- the 109 substrings,
    their order, abstaining rather than guessing -- lives in `rules.py`, which is
    also what `evals/score.py` scores. Anything added here that changed an
    answer would be logic the baseline number does not cover.
    """

    def categorize(self, request: CategorizeRequest) -> CategorizeResponse:
        answer = predict_by_rules(request.description)

        # The sentinel stops here. See CategorizeResponse's docstring for why it
        # must not be served: "unknown" is not one of the eleven, and the .NET
        # column would happily store it.
        category = None if answer == NO_PREDICTION else Category(answer)

        return CategorizeResponse(category=category, source=Source.RULES)
