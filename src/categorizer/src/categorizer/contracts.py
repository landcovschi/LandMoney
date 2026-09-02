"""What crosses the HTTP boundary, and the only place its shape is written down.

A pydantic `BaseModel` is a request record with DataAnnotations on it: the field
types and the `Field(...)` constraints are both the validation and the OpenAPI
schema, and FastAPI answers a violating body with 422 before the handler runs --
the same job `ValidationFilter<T>` does by hand on the .NET side.
"""

from decimal import Decimal
from enum import StrEnum

from pydantic import BaseModel, Field, field_validator

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


# The most rows one batch request may carry -- #93.
#
# A bound rather than a preference. #93's second trap: "a year of imports in one
# request is the same failure the per-row loop had, in a nicer coat" -- one HTTP
# call whose duration grows with the file, against a caller that has to give up on
# it eventually. Chunking is the caller's job; this is the number it has to chunk
# to, and going over it is a 422 rather than a request that quietly takes minutes.
#
# It lives here rather than in `main.py` because the .NET side has to know it: a
# sweep configured to send more would be refused, and a 422 reads from over there
# as the categorizer misbehaving rather than as a number in `appsettings.json`.
# `CategorizerBatchCapTests` reads this line the way `CategoriesTests` reads
# `categories.py`, which is this repository's answer to a constant that has to
# exist in two languages.
#
# A hundred at roughly 2.1 s a call (#60) is about 26 seconds at the default
# concurrency, which is what the .NET budget is chosen against.
MAX_BATCH_ITEMS = 100


class BatchItem(CategorizeRequest):
    """One row of a batch: a `CategorizeRequest`, plus the caller's own name for it.

    **Inherited rather than retyped, and that is #93's last trap answered by the
    type instead of by care.** A batch that answers positionally and drops a row
    shows up as one transaction categorised as its neighbour -- silently, and long
    afterwards. An id chosen by the caller makes a dropped row a *missing* row,
    which the caller can see, instead of a shifted one, which it cannot.

    Inheriting also means the per-row validation is the same declaration for one
    row as for a hundred: an amount refused by `POST /categorize` is refused here,
    with the same message, because it is the same field.

    The id is opaque to this service and is never logged. The .NET side sends a
    transaction id; nothing here may depend on that, and nothing here writes it
    down -- #64's rule about keeping the user's own data out of logs applies to the
    key as much as to the description it names.
    """

    id: str = Field(min_length=1, max_length=64)


class CategorizeBatchRequest(BaseModel):
    """Many rows in, one round trip. #93."""

    items: list[BatchItem] = Field(min_length=1, max_length=MAX_BATCH_ITEMS)

    @field_validator("items")
    @classmethod
    def _ids_must_be_unique(cls, items: list[BatchItem]) -> list[BatchItem]:
        """Two rows with one id have no unambiguous answer, so the request is refused.

        Answering both under one key loses one of them; answering one key twice
        makes the response a list the caller has to disambiguate, which is the
        positional bug wearing the id's clothes. A 422 naming the duplicate is the
        only outcome that cannot be misread.
        """
        seen: set[str] = set()
        duplicates: set[str] = set()

        for item in items:
            if item.id in seen:
                duplicates.add(item.id)

            seen.add(item.id)

        if duplicates:
            raise ValueError(
                f"ids must be unique within a batch; these repeat: {sorted(duplicates)}"
            )

        return items


class BatchAnswer(CategorizeResponse):
    """One answer, and the id of the row it answers.

    Inherited from `CategorizeResponse` for the reason `BatchItem` is inherited
    from `CategorizeRequest`: #93 asks that the per-row shape of the answer be
    preserved, and the cheapest way to preserve a shape is not to write it a second
    time. A field added to the single-row response is in the batch by construction.
    """

    id: str


class CategorizeBatchResponse(BaseModel):
    """The answers, in the order the items arrived.

    **There may be fewer answers than items, and that is the contract rather than
    an accident.** A predictor that raises on one row must not cost the other
    ninety-nine their answers, so that row is left out and logged; the caller sees
    an id it sent and did not get back, which is exactly the signal it needs in
    order to try again. A response padded with nulls would say "asked and got
    nothing", which is an abstention, and an abstention is a final answer.

    The order is the request's, which is a convenience and never the contract: the
    id is what pairs an answer with its row.
    """

    answers: list[BatchAnswer]
