"""The categorizer service -- #39.

Two consumers, one `predict`:

  * `evals/score.py` imports `rules` directly and scores it against the labelled
    set, which is where the baseline macro recall comes from.
  * `main.py` puts the same function behind `POST /categorize`, which is what
    the .NET application calls.

Keeping those the same code is the entire reason the rules moved out of
`evals/`. A service scored through a copy of its own logic reports a number
about the copy.

**No LLM call lives here, and that is a rule rather than an omission.**
`CLAUDE.md` carries it unchanged from netshift: the baseline has to exist and be
scored before a model is allowed to answer, so that "it got better" is a
measurement. The seam the model arrives through is `Predictor` in
`predictor.py`, and the field that will tell the two apart in stored data is
`source` in `contracts.py`. Both exist now, on purpose, because adding them
afterwards leaves rows that cannot say where they came from.
"""
