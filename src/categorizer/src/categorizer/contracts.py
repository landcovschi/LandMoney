"""What crosses the HTTP boundary, and the only place its shape is written down.

A pydantic `BaseModel` is a request record with DataAnnotations on it: the field
types and the `Field(...)` constraints are both the validation and the OpenAPI
schema, and FastAPI answers a violating body with 422 before the handler runs --
the same job `ValidationFilter<T>` does by hand on the .NET side.
"""

from decimal import Decimal
from enum import StrEnum

from pydantic import BaseModel, Field

from categorizer.categories import CATEGORIES

# The eleven, as an enum, built from the tuple rather than retyped beside it.
#
# Retyping them here is the mistake this line exists to prevent: `categories.py`
# says it is "the single place the list exists", and a schema holding a twelfth
# spelling would break that quietly -- the scorer would refuse the label while
# the API happily served it.
#
# The functional form is used because the member *names* have to be derived:
# `eating-out` is not an identifier, so the member is EATING_OUT and its value
# is the string the rest of the system uses. `module=__name__` is not
# decoration; without it the enum's __module__ is wrong and it cannot be
# pickled, which is the kind of thing that fails much later and far away.
Category = StrEnum(
    "Category",
    {name.replace("-", "_").upper(): name for name in CATEGORIES},
    module=__name__,
)


class Source(StrEnum):
    """How the answer was produced. The field #39 says must exist from day one.

    `MODEL` has no producer today and is declared anyway. That is the point:
    once rows carry a category, a column that cannot say whether a rule or a
    model wrote it can never be back-filled -- the information was never
    recorded. Adding the member later is a schema change; adding the *history*
    later is impossible.
    """

    RULES = "rules"
    MODEL = "model"


class CategorizeRequest(BaseModel):
    """A description, an amount and a currency in.

    The limits mirror `CreateTransactionRequest` on the .NET side exactly, and
    for the same reason that one mirrors `numeric(18,2)`: the boundary that can
    give the best error should be the one that says no.

    `amount` and `currency` are here although `rules.predict` reads neither --
    it matches substrings in the description and nothing else. They are in the
    contract because the model will use them (a 4.50 EUR and a 450 EUR "Bolt"
    are not the same purchase), and because a field added to a request after
    answers have been stored leaves every earlier answer unable to say what its
    producer was shown.
    """

    description: str = Field(min_length=1, max_length=500)

    # Decimal, never float -- the same rule as the .NET side and as
    # evals/score.py. What makes it true here rather than merely declared:
    # pydantic hands the JSON token straight to Decimal instead of parsing a
    # float first. That is checked rather than believed, and the check is
    # `decimal_places=2` itself -- 12.34 as a float is
    # 12.3399999999999998578..., which has far more than two places, so a float
    # intermediate would make an ordinary amount fail validation. A passing test
    # for 12.34 is therefore the measurement.
    amount: Decimal = Field(gt=0, max_digits=18, decimal_places=2)

    # The floor and the ceiling together, the way the .NET RegularExpression
    # does. `min_length=3` alone would accept "1$x".
    currency: str = Field(pattern=r"^[A-Za-z]{3}$")


class CategorizeResponse(BaseModel):
    """A category out, and how it was arrived at.

    `category` is null when the predictor had no answer, and that is a decision
    rather than a default. `rules.predict` returns the sentinel `"unknown"`,
    which `categories.py` keeps deliberately outside the vocabulary so the
    scorer always counts it as a miss. Serving that string would put a twelfth
    value into the application's `transactions.category` column -- the exact
    failure the closed vocabulary exists to prevent. The sentinel therefore
    stops at this boundary and nowhere earlier: `score.py` still sees it, so the
    baseline number is unchanged by the move.

    The cost, and it is real: over HTTP, "the rules had no answer" and "the
    service was unreachable" both reach the .NET side as a null category. Only
    the first one carries a `source`, which is the only thing distinguishing
    them, and the .NET client never stores it today.
    """

    category: Category | None
    source: Source
