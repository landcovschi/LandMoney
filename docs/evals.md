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

**Spent on 2026-08-29 in #66, and recorded as burned on 2026-09-02 in #91.**
This file is not an instrument any more. The section is kept in the shape of an
account rather than deleted, because one that quietly stopped describing a live
holdout would leave the project reading as though it still had one -- which is
exactly the mistake #91 was written under.

`evals/holdout.csv` was written on 2026-08-25 alongside the labelled set, for
one use: at the end of slice 4, label it once and score whatever won on it.
Tuning against the same rows until they pass teaches the answers, and the eval
set cannot report that it has happened.

### The two numbers

#66 released it -- slice 4 closed with #60 -- to give retrieval a corpus and an
eval that do not overlap, since a nearest-neighbour lookup that can return the
row it is answering is a lie with a very good percentage. Labelling it to do
that produced the scores a holdout exists to produce:

| predictor | macro recall | accuracy | date |
| --- | --- | --- | --- |
| rules | **44.4%** | 40.0% | 2026-08-29 |
| model, no retrieval | **100.0%** | 100.0% | 2026-08-29 |

Ten rows, `claude-opus-5` at `effort=low`, and both recalls are macro averages
over the **nine** categories present rather than over the eleven -- see below.
The model answered ten of ten with zero abstentions and zero confident errors;
the rules abstained on six of their six misses. Section 8 has the run itself and
what it says about retrieval.

**It was spent cleanly, and that is the half most easily lost.** Nothing was
tuned after the numbers were seen -- section 8 records the score floor that was
*not* added in response to seeing the neighbours, at the one place in
`retrieval.py` it would have gone. So the verdict stands rather than being
merely available: on rows neither predictor had been tuned against, the model
held at 100.0% and the rules fell 11.7 points below their 56.1% on the main set.

### Read it as small, and two categories short

Ten rows over **nine** of the eleven categories: `fees` and `other` do not
appear in the file at all, so neither number above is an average over the
vocabulary. That is not a detail, and it cuts both ways.

`other` is the category section 7 singles out -- structurally unreachable for a
substring baseline, and the one the model took from 0/3 to 3/3 on the main set.
So **the model's 100.0% is a hundred per cent of a set with the hardest category
missing**, which is the single largest reason to read it as agreement rather
than as a result. And the rules' 44.4% is *flattered* by the same absence: on
the main set they score `other` at 0.0% and `fees` at 33.3%, the two worst of
the eleven. They still came out 11.7 points below their own main-set number.

Eight of the nine categories present hold exactly one row, so each can only
score 0% or 100%; `score.py` named all nine as thin, under its three-row floor.
One row is 10 points of accuracy. "Does this broadly agree" is the whole of what
this file can say, and it agreed.

**A replacement inherits this problem and will not solve it either.** Real
spending is lopsided (#90's second trap), so a holdout sliced off it will be
thin or empty in the same categories the main set is thin in. Recording which
categories a holdout cannot measure is part of reporting its number, not a
footnote to it.

### What it cannot answer, and why there is no second run

The rows are synthetic, written by the same hand as the labelled set. A held-out
set drawn from the same synthetic distribution checks for **tuning**, which is
what it is for, and does not check that anything generalises to real
descriptions -- so it was never able to answer the question section 7 raises
about itself.

It cannot be re-run to answer it later either. The rows have been seen, and rows
that have been seen are not held out; a second run would report a number about
an instrument that no longer exists, and reusing it while saying so is worse
than not using it at all.

### The replacement, and the one moment it can be taken

**A slice of #90's real export, held back before the labelling session rather
than after it.** Agreed 2026-09-02. That ordering is the whole of it: once a set
has been scored against, no part of it is a holdout any more, so carving one out
retroactively produces rows that were already seen rather than rows nobody has
looked at.

`score.py` refuses to score a file whose labels are blank, so a replacement left
unlabelled cannot be spent by accident -- which is what protected this one until
something deliberately released it.

### The file is named for what it is

**`evals/holdout.csv` is now `evals/holdout-spent-2026-08-29.csv`.** A note in a
document is a thing a reader has to have read; a path that no longer resolves is
a thing the shell says. So the `--set evals/holdout.csv` commands printed in
section 8 -- and any copied out of them -- now fail with a missing file rather
than quietly scoring a burned set for a second time, which is the failure this
whole section exists to prevent.

It also leaves the name `holdout.csv` free for the replacement, and that is safe
rather than a trap: a replacement is held back **unlabelled**, and `score.py`
refuses a file with blank labels. An old command pointed at a new file therefore
refuses instead of scoring the wrong thing.

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

**60.8% was what every model had to beat**, until the set was replaced on
2026-08-26. Section 6 has the number that stands; this one is kept because it
is what the misses below are about.

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

## 6. The second set, and the number that stands

Written 2026-08-26 for #47, which asked for the rows of section 5 to be replaced
with real spending.

**They are still not real spending.** The owner instructed Claude to produce
them rather than supply history, the same instruction that produced the first
set, so the central defect #47 exists to remove is still there and **#47 stays
open.** What follows is an honest account of what did change, because a second
synthetic set that pretends to be the fix is worse than the first one.

### Where the rows came from

53 labelled rows and 10 held out, written by Claude on 2026-08-26.

The one source of evidence about how the owner really types was the deployed
database, which held four rows and three of them were deploy smoke tests:

```
2026-08-26   7.20 EUR  Coffee after a green deploy
2026-08-25 200.00 EUR  cofee
2026-08-25  12.50 EUR  First transaction entered in Azure
2026-08-25  12.00 EUR  Makaronu (Cyrillic in the original)
```

Four rows cannot found an eval set. They are enough to show *how* a description
gets typed -- lower case, no punctuation, a typo left in -- and that shape is
what the new rows copy.

### What changed, and it is less than #47 asked for

- **Descriptions are as typed rather than as written up.** Lower case, no
  trailing punctuation, and `cofee` carried over verbatim from the database
  against `coffee beans 1kg`. The first set's `Latte and a croissant` and
  `Bakery, bread and buns` are the voice of somebody composing an example.
- **Merchant names are the real ones**: `linella`, `kaufland`, `fidesco`, `nr1`,
  `la placinte`, `starnet`, `felicia pharmacy`, where the first set had
  `Supermarket`, `Bakery` and `Market`.
- **The currency mix is Moldovan** -- 49 MDL, 3 EUR, 1 USD -- rather than spread
  for variety.
- **Eight rows more**, 53 against 45, and the counts tilt toward the categories
  a person actually meets weekly: `groceries` 8 and `eating-out` 7 against
  `fees`, `gifts` and `other` at the floor of 3.

### What did not change, and one thing that got worse

1. **The label distribution is still chosen, not observed.** #47 requires at
   least three rows in every category and `score.py` warns below that, so the
   set cannot be tilted to a realistic shape without breaking the rule that
   makes the macro average readable. This is a genuine conflict between the
   metric and realism, and the metric wins while the set is this small.
2. **The labeller and the measured thing still share an author.** The owner
   reviewed the labels, which is worth something and is not the same thing.
3. **Every description is still English, and this is now a decision rather than
   an oversight.** A first pass wrote them in Russian and Romanian -- which is
   how they would really be typed, and which is exactly what section 3 predicts
   would send the baseline close to zero. The owner ruled that everything in the
   repository is English, per `CLAUDE.md`. So the single most likely way this
   baseline reads optimistic is now deliberately preserved, and the day it is
   tested is the day that rule is relaxed for `evals/*.csv` alone.

That third point is the one to weigh: in English, at 53 rows, a plausible
personal-finance set converges on the set it replaces. Seven descriptions
survive from the first set unchanged -- `haircut`, `dry cleaning`, `blood
tests`, `oil change`, `parking fine`, `flowers`, `netflix` -- not by oversight
but because there is no other way to write them.

### The number

Rules as they stand, no rule edited after seeing a miss, `score.py` run once
after labelling was finished:

```
accuracy       56.6%   (30 of 53)
MACRO RECALL   56.1%   <-- the number
```

**56.1% is what every model has to beat.**

It moved down 4.7 points, which #47 predicted and called the point of the
exercise rather than a failure of it. Read that drop carefully, though: section
2 point 5 puts the noise floor at about 3 points on a set this size, and these
are **two different sets** rather than two runs over one, so the honest reading
is "the same baseline, re-measured against harder rows", not "the baseline got
4.7 points worse".

The structure of the misses is unchanged, which is the useful part:

- **22 of the 23 misses are abstentions**, against 16 of 17 before. Substring
  matching still fails here by not covering rather than by being wrong, and the
  new merchant names (`linella`, `fidesco`, `nr1`) widen the gap exactly as
  expected -- an English substring list has never heard of them.
- **The one confident error is `parking fine` -> `transport` again**, `parking`
  still sitting above `fine`. The collision was preserved on purpose when the
  rows were written, and the rule is still left alone.
- **`other` still scores 0% and still structurally cannot score anything else**,
  so the 90.9% hard ceiling on any abstaining substring baseline is unchanged.

## 7. The model, and the number it beat the baseline by

Written 2026-08-28 for #60, step 5 of slice 4 -- the measurement every other
issue in this project exists to make possible. It is the first time a request
from this repository has been accepted by the Anthropic API; #59 shipped the
adapter against a deliberately broken key and said so, and #76 provisioned the
real one.

### What was measured, and with what

Both predictors, the same 53 rows, the same day, the same code, one scorer:

```
python evals/score.py --confusion --misses
uv run --project src/categorizer python evals/score.py --predictor model --confusion --misses
```

The model run is `claude-opus-5`, `effort=low`, `max_tokens=2048`, a 6-second
per-call timeout, and the system prompt in `src/categorizer/src/categorizer/prompt.py`
at **sha256:c8ad9d9fd16f**. The scorer prints that fingerprint in its own header
beside the model id, which is what makes a recorded number reproducible: an
edited prompt changes the digest, so a later run visibly disagrees about what was
measured instead of silently agreeing.

**The prompt was not edited.** #60 permits tuning it after seeing which rows
missed, provided that is said out loud; nothing was tuned, and the number below
is the first and only configuration that was run.

**Both runs were live calls, and since #65 that is something the scorer enforces
rather than something the day happened to be.** The service now caches model
answers in Redis so an identical description is not billed twice, and
`score.py` erases `CATEGORIZER_REDIS_URL` from the environment it hands the
adapter unless `--cache` is passed. It has to erase rather than merely not pass
it, because the sanctioned way to run this with a real key is
`set -a; . ./.env; set +a`, which exports everything in the file. The reason is
the sentence above about two identical runs: they stop being evidence of anything
the moment the second one can read the first. A cached run is still worth having
after a change to the *scorer* -- that is what the flag is for -- and it is never
what a recorded number may come from.

### The number

```
                 rules      model     delta
macro recall     56.1%      98.9%     +42.8
accuracy         56.6%      98.1%     +41.5
abstained        41.5%       1.9%     -39.6
confident errors     1          0
```

**98.9% against 56.1%.** Section 2 point 5 puts the noise floor at about 3
points on a set this size; 42.8 is not near it.

The model was run **twice**, and the two runs are identical -- same macro recall,
same accuracy, same single missed row. That is not proof of determinism and two
samples cannot estimate a variance, but it does rule out the reading where a
single lucky run carried the result.

Per category, the model is 100% everywhere except `groceries` at 87.5%.

### The failures, which are more interesting than the percentage

This is section 5's point 3 taken seriously: the metric charges the same for an
abstention and a confident error, and the two are not the same system.

- **One miss in 53, and it is an abstention.** `fidesco`, line 33, declined in
  both runs.
- **Zero confident errors.** The model never wrote a wrong category. The rules
  wrote one -- `parking fine` -> `transport` -- and it is the failure that
  matters most on the .NET side, because a wrong category is stored as if it
  were true while a null is a state the application already handles.
- **The confusion matrix is a clean diagonal plus one cell.** Nothing was
  confused *for* anything; the only off-diagonal count is the abstention.

`fidesco` is a Moldovan supermarket chain, and it is the same class of failure
the rules have -- a proper noun carrying no signal about what was bought. The
difference is coverage: the model resolved `linella`, `kaufland` and `nr1 water
6l`, which the rules missed alongside it. Declining rather than guessing is what
the prompt instructs, and it is the behaviour worth having: the row cost one
point of recall and stored nothing false.

### The `other` question, which #60 asked specifically

**`other` goes 0/3 to 3/3.** Its rows are `haircut`, `dry cleaning` and `parcel
by post` -- services, which a substring list over merchant names cannot reach
without enumerating every service anybody might buy.

That is the whole of the answer to "is it doing the thing rules cannot". Section
6 records a **90.9% hard ceiling** on any abstaining substring baseline, because
one category of eleven is structurally unreachable. The model scored 98.9%,
which is *above that ceiling* -- so the improvement is not the same baseline
tuned further, it is a predictor that is not subject to the constraint.

### What this number is not

Three caveats, and the first is the one that matters most.

1. **The eval set was written by Claude, and the model being scored is Claude.**
   Section 6 records that #47 asked for real spending and did not get it; the 53
   rows are invented. So an English-language set authored by one Claude model and
   answered at 98.9% by another is uncomfortably close to grading its own
   homework -- the descriptions are drawn from the same distribution as the
   answers. Nothing here separates "the model understands personal spending"
   from "the model recognises the phrasing another model would choose". **This
   is the single strongest reason to distrust 98.9%**, it cannot be fixed by
   re-running anything, and it is fixed only by #47 -- real rows, from the
   owner's own history.
2. **The descriptions are all English, deliberately** (section 6, point 3). Real
   entries would be Russian and Romanian. That is the second most likely way this
   reads optimistic, and it is preserved on purpose by the repository's
   English-only rule.
3. **53 rows, 3 to 8 per category.** `score.py` still warns below 3, and a single
   row is 1.9 points of accuracy. A 42.8-point gap survives that easily; a future
   comparison between two models would not.

`evals/holdout.csv` was **not touched**, and remains unlooked-at. It is the only
thing left that can answer a question this section cannot.

**That was true on the day and stopped being true the next one.** #66 released
and spent it on 2026-08-29; section 4 now carries the two numbers and marks the
file burned. The sentence above is left standing rather than corrected, because
what it claims about *this* section is still exactly right -- #60 did not look --
and because the record is worth more showing a gap that was closed than one that
was quietly edited shut.

### Operational facts worth keeping

- **53 calls, 114 s and 115 s** for the two runs -- about 2.1 s each, comfortably
  inside the 6-second timeout, which is why nothing failed. The measurement was
  therefore taken at the timeout the *service* runs, not at a relaxed one; a
  number produced under a configuration that is not deployed would describe
  something that does not exist.
- **Zero failed calls in either run**, so the ERROR-counting guard never fired.
  That guard is what makes the number trustworthy rather than merely present: a
  failed call is indistinguishable from an abstention, so a run with failures
  would have read as a worse model rather than as a broken run.
- **`evals/baseline.json` still records the rules**, and deliberately. It is what
  CI asserts on every pull request, `check` refuses to compare across predictors,
  and the model must never run on a pull request -- it costs money per row and the
  required check would become a bill. The model's number lives here, in prose,
  where it can carry the caveats above; a JSON file cannot say "the set was
  written by the thing being measured".

## 8. Retrieval, and the eval set running out of room

Written 2026-08-29 for #66, which asked for the user's own history to be used as
few-shot examples and for the number from section 7 to move "in whichever
direction, and both numbers recorded". Both numbers are below. Neither moved,
and the reason is the most useful thing this section has to say.

### The measurement is out of headroom, and that is the finding

Section 7 put the model at **98.9% macro recall on `transactions.csv`** -- one
miss in 53, the abstention on `fidesco`. So before anything was built, the
arithmetic said:

| | today | perfect | headroom |
| --- | --- | --- | --- |
| macro recall | 98.9% | 100% | **+1.1** |
| accuracy | 98.1% | 100% | +1.9 |

Section 2 point 5 puts the noise floor on a set this size at about **3 points**.
An improvement of at most 1.1 cannot be distinguished from noise. **This set can
detect retrieval harming the model and is structurally unable to detect it
helping.**

`holdout.csv` was labelled for #66 (section 4 releases it at the end of slice 4,
which closed with #60) specifically to give a corpus and an eval that do not
overlap -- #66's second trap, since a nearest-neighbour lookup that can return the
row it is answering is a lie with a very good percentage. It turned out to have
even less room:

```
                     transactions.csv (53)   holdout.csv (10)
rules                        56.1%                44.4%
model, no retrieval          98.9%               100.0%
model, lexical retrieval       not run           100.0%
model, vector retrieval        not run           not run
```

**The model scores 100.0% on the holdout with no retrieval at all** -- ten of ten,
zero abstentions, zero confident errors. There is no number left to improve.

That the harder-for-the-rules set (44.4% against 56.1%) is the easier-for-the-model
set is worth a sentence: the rules fail on proper nouns and non-English words
(`benzin full tank`, `minibus`), and those are exactly what a model reads without
difficulty. Substring difficulty and semantic difficulty are not the same axis, and
this set is the clearest demonstration of it so far.

### What was measured, and what it says

Lexical retrieval over the 53-row corpus, scored on the holdout: **100.0% macro
recall and 100.0% accuracy, unchanged**. That is not "retrieval helped". It is the
one thing this data can report, and it is worth having, because the free inspection
run beforehand predicted it might go the other way:

```
python evals/score.py --set evals/holdout.csv --corpus evals/transactions.csv \
    --retrieval lexical --show-examples
```

```
heating  [housing]
    0.188  headphones  -> shopping
    0.105  parking fine  -> fees
    0.105  dry cleaning  -> other
corner shop  [groceries]
    0.125  cofee  -> eating-out
    0.115  shelter donation  -> gifts
    0.111  t-shirt  -> shopping
```

**Trigram retrieval on this corpus is mostly noise.** Scores sit around 0.1, and
the only genuine hit in ten rows is `minibus` -> `trolleybus`. The model was shown
five confidently-labelled, mostly-irrelevant rows for nearly every transaction and
got all ten right anyway. So the finding is about the **prompt** rather than about
retrieval: the paragraph telling the model that the rows were chosen by similarity
rather than relevance, that some may be irrelevant, and that being shown examples
is never a reason to stop answering `unknown`, is doing its job. Without that
paragraph this run is the obvious way to make a 100% into a 90%, and it was written
before the run rather than after it.

**No score floor was added in response to seeing those numbers**, and that is a
decision rather than an oversight. Dropping neighbours below some similarity would
plainly have tidied the output above. Choosing the threshold by looking at the eval
set is exactly what `holdout.csv` exists to catch, and `retrieval.py` says so at
the one place it would go. The single exception is `LexicalStore` discarding rows
that score exactly zero, which is not a threshold: a trigram score of zero means
the two descriptions share no three consecutive characters at all, so the row is
not a weak match but not a match.

### What was not measured, and why

**The vector arm has not been run.** It is implemented, tested and switched on by
one setting, and it needs a `VOYAGE_API_KEY` -- Anthropic has no embedding model
and its own documentation points at Voyage AI. Provisioning that key is the
owner's act, the way #76 was for the Anthropic one. The first 200 million tokens
are free per account, so the arm costs nothing but the signing up.

**`transactions.csv` was not re-scored with retrieval.** 53 rows is 53 calls,
about USD 0.60, to move a number whose maximum possible movement is 1.1 points
against a 3-point noise floor -- and the only corpus available to retrieve from
would be the 10-row holdout. It is the reading to buy **after** #47, not before.

### What this means for #66, said plainly

The mechanism works, is inspectable and is off by default. What cannot be claimed
is that it helps, and no configuration of the data this project currently holds
could establish that it does. #66 says the honest outcome may be "it did not
help"; the honest outcome here is one step short of that -- **"it did no harm, and
nothing here can measure whether it helps"**.

The blocker is not retrieval and never was. It is that **the eval set is
saturated**: a predictor at 98.9% and 100% has nothing left to demonstrate on 63
rows written in English by the same kind of system being scored. #47 -- real rows,
in Russian and Romanian, which #62's CSV import exists to feed -- is the
prerequisite for every future claim about the categorizer getting better, and it is
now the single most valuable open item in the project. Section 6's warning that the
English descriptions are "the single most likely way this baseline reads
optimistic" has become the thing that stops work rather than a caveat on it.

`baseline.json` is untouched. It records the rules on `transactions.csv`, CI
asserts it on every pull request, and none of the above is a number a required
check may depend on.
