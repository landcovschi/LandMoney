# Evals

Written 2026-08-24 for #25, **before the first prediction of any kind** -- the
rules baseline in `evals/rules.py` was written before a single row was
labelled, and no model has been called at all.

This is the rule carried over from netshift unchanged: no LLM call before evals
exist. Without a number, "it got better" is a feeling.

**One thing about this set changes how to read every number below, added
2026-08-25.** `evals/transactions.csv` does not hold real spending. #25 asked
for 30-50 transactions out of the owner's own history, and PR #44 left the file
empty on the argument that only the owner can produce them; on 2026-08-25 the
owner asked Claude to write and label the rows instead. What that costs is
section 5, and it is not small. Everything else in this file was written before
any of it and is unchanged.

The code lives in `evals/`. This file holds the two decisions that are worth
more than the code and would otherwise be re-argued from scratch: **what the
categories are**, and **what the number means**.

## 1. The category vocabulary

Eleven categories, closed, decided before any labelling. Closed is the whole
point: with an open vocabulary two reasonable answers to the same transaction
are both right, and there is nothing left to score.

The list is in `evals/categories.py`, which is the one place it exists -- the
scorer refuses a CSV containing anything not in it, so a typo is an error
rather than a twelfth category.

| Category | What belongs in it |
| --- | --- |
| `groceries` | Food and household consumables bought to take home: supermarket, market, bakery, cleaning supplies |
| `eating-out` | Food and drink consumed away from home: restaurant, cafe, bar, takeaway, delivery |
| `transport` | Moving a person around: fuel, taxi, public transport, parking, car servicing, insurance for the car |
| `housing` | Costs attached to the flat: rent, electricity, gas, water, heating, home internet, repairs |
| `health` | Pharmacy, doctor, dentist, optician, medical insurance |
| `shopping` | Durable goods for personal use: clothes, shoes, electronics, furniture, homeware |
| `subscriptions` | Recurring services attached to the person: streaming, cloud, software, mobile plan, gym |
| `leisure` | Experiences: cinema, concerts, books, games, hobbies, **and travel** |
| `gifts` | Money or goods for someone else, and charity |
| `fees` | Bank charges, transfer and conversion fees, taxes, fines, government fees |
| `other` | Genuinely fits nothing above |

### The boundary rules

A vocabulary is only closed if the awkward rows have one answer. These are the
cases that would otherwise be decided differently on different days, which is
label noise, and label noise is a ceiling below 100% that nothing in the number
tells you about.

- **Home internet is `housing`; the mobile plan is `subscriptions`.** The test
  is what the service is attached to -- one follows the flat, the other follows
  the person.
- **A holiday is `leisure`, including the flight and the hotel.** A flight is
  transport by the literal reading, and that reading splits one trip across two
  categories depending on which line of the receipt is being looked at.
  `transport` is routine movement; a trip is an experience.
- **Coffee beans from a supermarket are `groceries`; a coffee in a cafe is
  `eating-out`.** Where it is consumed, not what it is.
- **A book is `leisure`, a laptop is `shopping`.** Consumed once versus owned.
- **Car insurance is `transport`, health insurance is `health`.** Insurance is
  not a category; it is an attribute of the thing insured.
- **`other` is for a row that fits nothing, never for a row that is hard to
  place between two.** Those are what the rules above are for. A set where
  `other` is large means the vocabulary is wrong, and that is a finding, not a
  label.

### What was left out, and why

**`travel` as its own category, and `education` as its own category.** Both are
real kinds of spending and both lost to the same argument, which comes from the
metric below rather than from taste: **macro recall averages the categories
without weighting them**, so a category holding two rows moves the final number
exactly as much as one holding fifteen. With 40 rows across 11 categories the
average category has under four rows, and one row is then 25 points of that
category's recall. A category with two rows is not measured, it is a coin flip
that the average takes seriously. Fewer, larger categories is what the size of
the set can support.

The rule of thumb that follows: **aim for at least three rows per category and
five where possible**, and read a category with fewer than three as undecidable
rather than as a score. `score.py` prints a warning naming any category below
three, which is a property of the eval set and not of the predictions.

**Income.** `Transaction` is documented as "one item of spending". A salary is
not a categorisation problem this application has.

## 2. The metric

**Macro-averaged recall.** Per category, the share of that category's rows that
were predicted correctly; then the plain unweighted mean over the categories
that appear in the labelled data.

```
recall(c)    = rows correctly predicted c / rows whose true label is c
macro recall = mean of recall(c) over every c present in the gold set
```

Categories in the vocabulary that no row carries are not in the average -- a
category nobody spent money in cannot be recalled, and averaging a 0% in for it
would make the number depend on how many categories were declared rather than
on the answers.

**One number, and it is this one.** `score.py` prints accuracy and a
per-category table beside it, and those are for diagnosis. The number quoted in
a pull request is the macro recall.

### Why not plain accuracy

Because of the failure #25 named: with one category covering 40% of the rows, a
system that always guesses that category scores 40% and has learned nothing.
Macro recall scores that same system at 1/11 = 9%, because the other ten
categories each contribute a zero. `evals/test_score.py` asserts exactly this on
a hand-built example, so the claim is checked rather than believed.

### Why recall rather than F1, or precision

The obvious objection to recall alone is that it cannot see over-prediction: a
system answering `groceries` to everything has perfect recall *for groceries*.
That is answered by the averaging rather than by a second term. The same system
scores 100% on one category and 0% on the other ten, so the macro number lands
at 9% -- the over-eager category is punished through the recall it destroys
elsewhere, which is the effect precision would have measured and one fewer
number to explain.

Macro-F1 is the more complete answer and is what to reach for the day this
number stops discriminating between two candidates. It was not worth the
explanation on day one.

### What this metric does not capture

Required by #25, and the half of a metric that usually goes unwritten.

1. **Every wrong answer costs the same.** Filing a restaurant bill under
   `groceries` and filing it under `fees` are the same miss. The first is a
   system that nearly understands and the second is one that does not, and the
   number cannot tell them apart. The per-category table and the confusion pairs
   `score.py` prints exist for this; the single number does not.
2. **It ignores how often a category actually occurs.** That is deliberate -- it
   is the whole reason for macro over accuracy -- but it means a gain in macro
   recall is **not** the same as a drop in the rows the owner fixes by hand. If
   the gain is all in a category met twice a year, the weekly experience is
   unchanged. Accuracy is printed beside it as the answer to that second
   question.
3. **It says nothing about abstention.** A system answering "I do not know"
   scores identically to one answering confidently and wrongly, though the two
   cost very different amounts of attention. The rules baseline abstains a lot
   (see below), so this is live from the first run rather than theoretical.
4. **It cannot see the ceiling.** Where the owner would label the same row two
   ways on two different days, nothing can score 100%, and the number does not
   say where the real ceiling is. The boundary rules above exist to push that
   ceiling up; they do not measure it.
5. **The set is small, so small differences are noise.** At roughly four rows
   per category, one row is ~25 points of that category's recall and therefore
   ~2.3 points of the macro number. **Treat a difference under about 3 points as
   nothing having happened.** This is the number to remember the first time a
   prompt change "improves" the score.

## 3. The rules baseline

`evals/rules.py`: an ordered list of `(substring, category)` pairs, matched
case-insensitively against the description, **first match wins**. Whatever it
scores is what every model has to beat.

Three decisions in it that are not obvious.

**Order is load-bearing, and it is the point of the exercise.** `coffee` matches
a supermarket bag of beans and a cup in a cafe, and one list cannot be right
about both. The list runs most specific to least, that ordering is part of the
baseline, and the rows it gets wrong are the honest ceiling of substring
matching rather than a bug to fix.

**No match predicts `unknown`, which is not in the vocabulary and is therefore
always a miss.** The alternative -- fall back to the most common category -- was
rejected because it makes the baseline's score a fact about the label
distribution of the eval set rather than about the rules: relabelling more rows
`groceries` would move the baseline without a rule changing. Falling back to
`other` lost to a smaller version of the same argument, that it scores `other`
rows right for the wrong reason and makes the one category whose size is a
warning sign look healthy.

**The rules were written before a single row was labelled.** That is the
strongest available form of the trap #25 names -- rules tuned against the fifty
rows they are scored on have been taught the answers. When the set is labelled
the rules are scored **as they stand**, and that first number is the baseline.
Editing a rule after seeing which rows it missed produces a different, weaker
number, and if it is done it has to be said out loud beside the result.

**A limitation worth knowing before the first run:** the substrings are English,
because everything in this repository is. If the descriptions are typed in
Russian or Romanian the baseline scores close to zero, and that is a real
finding rather than a broken script -- it is the strongest argument the model
half of slice 4 will ever get, and it belongs in the record rather than being
patched around by quietly adding non-English substrings.

## 4. The held-out rows

`evals/holdout.csv` carries descriptions with **an empty category column** and
is not to be labelled now. It exists for one use: at the end of slice 4, label
it once and score whatever won on it. Tuning against the same rows until they
pass teaches the answers, and the eval set cannot report that it has happened.

`score.py` refuses to score a file whose labels are blank, so using the holdout
early has to be deliberate rather than accidental.

The eight rows in it were written on 2026-08-25 by the same hand as the labelled
set and carry the same caveat: a held-out set drawn from the same synthetic
distribution checks for tuning, which is what it is for, and does not check that
anything generalises to real descriptions.

## 5. The set, and the first baseline number

Written 2026-08-25, after sections 1 to 4 and after the rules.

### Where the rows came from

45 labelled rows in `evals/transactions.csv` and 8 unlabelled in
`evals/holdout.csv`, written and labelled by Claude on the owner's explicit
instruction, PR #44 having left both files empty a day earlier on the argument
that only the owner can produce them.

**They are plausible, not real**, and the difference is the whole of #25's
second paragraph: the domain is personal finance precisely so that the owner can
judge every row without looking anything up, and a row nobody spent cannot be
judged that way. Four things the number below therefore does not mean, on top of
the five section 2 already lists.

1. **The label distribution is chosen, not observed.** The rows were laid out to
   put three to five in every category, because macro recall reads a two-row
   category as a coin flip. Real spending is nothing like flat -- `groceries`
   and `eating-out` would dominate and `gifts` would be a couple of rows a year
   -- so the macro number here is measured against a shape that will not recur.
2. **The terse rows are one person's idea of terse, and it is the wrong
   person's.** `Shaorma`, `Internet` and `Haircut` are here as the awkward ones.
   The genuinely awkward descriptions are the abbreviations somebody invents for
   themselves, and those cannot be guessed from outside.
3. **Every description is English**, so the limitation in section 3 -- that a
   Russian or Romanian description scores near zero -- is written down and still
   untested. It is the single most likely way this baseline reads optimistic.
4. **The labeller and the thing being measured share an author.** The rows were
   written before `rules.py` was opened, which is what keeps the baseline from
   having been tuned to them; it does nothing about a model in slice 4 being
   scored against labels a model wrote.

The fix is not a better synthetic set. It is replacing these rows with real
ones, re-running the baseline, and letting the new number replace the one below.
Nothing in `evals/` has to change for that: the loader, the metric and the rules
do not know where a row came from.

### The number

First run, rules as they stand, no rule edited after seeing a miss:

```
accuracy       62.2%   (28 of 45)
MACRO RECALL   60.8%   <-- the number
```

**60.8% is what every model has to beat.**

Three things the per-row misses say, all of them about the baseline rather than
about the set.

- **16 of the 17 misses are abstentions rather than confusions.** Substring
  matching on this vocabulary is mostly a coverage problem: `Blood tests`,
  `Oil change`, `Winter boots` and `Dry cleaning` match nothing at all. Exactly
  one row came back confidently wrong.
- **That one is `Parking fine` -> `transport`.** `parking` sits above `fine` in
  the list and wins. It is the ordering collision section 3 calls part of the
  baseline rather than a bug, and it is left alone.
- **`other` scores 0% and structurally cannot score anything else.** `rules.py`
  has no `other` rules, because a substring meaning "fits nothing above" does
  not exist. Across eleven categories that is a hard ceiling of 90.9% on any
  abstaining substring baseline here, and it accounts for 9.1 of the 39.2 points
  this one is missing.
