"""The rules baseline: substring matching on the description, first match wins.

Whatever this scores is the number every model has to beat. It is often
embarrassingly hard to beat, and that is the exercise rather than a failure of
it.

**Written before a single row was labelled**, which is the strongest available
answer to the trap #25 names: rules tuned against the rows they are scored on
have been taught the answers. When the set is labelled, this file is scored as
it stands, and that first number is the baseline. Editing a rule after seeing
which rows it missed produces a weaker kind of number and has to be said out
loud beside the result.

Deliberately no brand names. Real descriptions are full of them and they are
the cheapest accuracy there is, but they are also the owner's shops rather than
anything general, so adding them here after seeing the data is exactly the
tuning described above. If they go in, they go in with the score before and
after.
"""

from typing import Final

from categories import NO_PREDICTION

# Ordered most specific to least. **The order is part of the baseline**, not an
# implementation detail: `coffee` matches a supermarket bag of beans and a cup
# in a cafe, and no single list is right about both. The rows this gets wrong
# are the honest ceiling of substring matching.
#
# Seven pairs below exist only because of that ordering, and each one is a real
# collision rather than an invented illustration:
#
#   "gas station" before "gas"        -- fuel, not the heating bill
#   "car rental"  before "rent"       -- a car, not the flat
#   "coffee beans" before "coffee"    -- taken home, not drunk out
#   "notebook"    before "book"       -- a laptop, not reading
#   "headphones"/"phone case" before "phone" -- an object, not a plan
#   "bus ticket"  before "ticket"     -- a fare, not a concert
#   "taxi"        before "tax"        -- the transport block simply runs first
#
# Bare "bus" is not a rule at all, because it matches "business". That is the
# same class of mistake as the seven above and there is no ordering that fixes
# it; only a narrower substring does.
RULES: Final[tuple[tuple[str, str], ...]] = (
    # --- Specific, and swallowed by a later rule if moved down ---
    ("coffee beans", "groceries"),
    ("car rental", "transport"),
    ("car insurance", "transport"),
    ("gas station", "transport"),
    ("health insurance", "health"),
    ("phone case", "shopping"),
    ("headphones", "shopping"),
    ("notebook", "shopping"),
    ("bus ticket", "transport"),
    ("bus fare", "transport"),
    # --- groceries ---
    ("supermarket", "groceries"),
    ("grocer", "groceries"),
    ("market", "groceries"),
    ("bakery", "groceries"),
    ("butcher", "groceries"),
    ("bread", "groceries"),
    ("milk", "groceries"),
    ("vegetables", "groceries"),
    ("detergent", "groceries"),
    ("shampoo", "groceries"),
    # --- eating-out ---
    ("restaurant", "eating-out"),
    ("cafe", "eating-out"),
    ("coffee", "eating-out"),
    ("breakfast", "eating-out"),
    ("lunch", "eating-out"),
    ("dinner", "eating-out"),
    ("pizza", "eating-out"),
    ("burger", "eating-out"),
    ("sushi", "eating-out"),
    ("takeaway", "eating-out"),
    ("delivery", "eating-out"),
    # "bar" is kept although it matches "barber" and "bargain", because it is
    # what someone actually types. The baseline is allowed to be wrong here;
    # patching it after seeing a row it broke on is the tuning this file exists
    # to avoid.
    ("bar", "eating-out"),
    # --- transport ---
    ("fuel", "transport"),
    ("petrol", "transport"),
    ("diesel", "transport"),
    ("taxi", "transport"),
    ("uber", "transport"),
    ("bolt", "transport"),
    ("metro", "transport"),
    ("tram", "transport"),
    ("parking", "transport"),
    ("train", "transport"),
    ("car wash", "transport"),
    ("tyre", "transport"),
    ("service the car", "transport"),
    # --- housing ---
    ("rent", "housing"),
    ("mortgage", "housing"),
    ("electricity", "housing"),
    ("water bill", "housing"),
    ("heating", "housing"),
    ("gas bill", "housing"),
    ("gas", "housing"),
    ("utilities", "housing"),
    ("internet", "housing"),
    ("plumber", "housing"),
    ("electrician", "housing"),
    # --- health ---
    ("pharmacy", "health"),
    ("dentist", "health"),
    ("doctor", "health"),
    ("clinic", "health"),
    ("optician", "health"),
    ("medicine", "health"),
    ("vitamins", "health"),
    ("analysis", "health"),
    # --- shopping ---
    ("clothes", "shopping"),
    ("shoes", "shopping"),
    ("jacket", "shopping"),
    ("t-shirt", "shopping"),
    ("laptop", "shopping"),
    ("monitor", "shopping"),
    ("charger", "shopping"),
    ("furniture", "shopping"),
    ("towels", "shopping"),
    # --- subscriptions ---
    ("subscription", "subscriptions"),
    ("netflix", "subscriptions"),
    ("spotify", "subscriptions"),
    ("youtube", "subscriptions"),
    ("icloud", "subscriptions"),
    ("mobile", "subscriptions"),
    ("phone", "subscriptions"),
    ("gym", "subscriptions"),
    ("hosting", "subscriptions"),
    ("domain", "subscriptions"),
    # --- leisure ---
    ("cinema", "leisure"),
    ("movie", "leisure"),
    ("concert", "leisure"),
    ("museum", "leisure"),
    ("theatre", "leisure"),
    ("hotel", "leisure"),
    ("flight", "leisure"),
    ("airline", "leisure"),
    ("holiday", "leisure"),
    ("ticket", "leisure"),
    ("book", "leisure"),
    ("game", "leisure"),
    # --- gifts ---
    ("gift", "gifts"),
    ("birthday", "gifts"),
    ("charity", "gifts"),
    ("donation", "gifts"),
    ("flowers", "gifts"),
    # --- fees ---
    ("bank fee", "fees"),
    ("commission", "fees"),
    ("transfer fee", "fees"),
    ("exchange", "fees"),
    ("atm", "fees"),
    ("tax", "fees"),
    ("fine", "fees"),
    ("penalty", "fees"),
    ("notary", "fees"),
)


def predict(description: str) -> str:
    """Return the first matching rule's category, or NO_PREDICTION.

    Case-insensitive. `str.casefold` rather than `str.lower` because it folds
    the cases `lower` leaves alone, and -- unlike C#'s `ToLower()` -- neither of
    them consults a culture, so there is no invariant-culture question to get
    wrong here the way there is on the .NET side of this repository.
    """
    haystack = description.casefold()
    for needle, category in RULES:
        if needle in haystack:
            return category
    return NO_PREDICTION
