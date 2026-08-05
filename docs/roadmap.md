# Roadmap

LandMoney tracks personal spending. The application is real -- the owner is
meant to actually use it -- but the goal behind it is a move from .NET
development into AI engineering with devops skills.

Started 2026-08-05.

## Why this shape, and what went wrong before

The predecessor, `netshift`, is a working CLI that inspects legacy `.csproj`
files. It is paused, not deleted. It delivered Python, Docker and CI, and then
stalled, for reasons worth keeping:

- **No user.** Nobody asked for a `.csproj` inspector, so the goal never felt
  legible. LandMoney has a user -- the owner, weekly.
- **No deployment.** Three of the four things the owner wanted were Python,
  Docker and CI/CD. The first three were delivered; nothing was ever deployed
  anywhere, and the roadmap had no plan to.
- **The AI was three phases away.** Weeks went into the infrastructure half
  that a .NET developer with devops interest already understood, translated
  into different function names. The mistake was recorded in netshift's own
  `docs/why-this-project.md` days before it was repeated.

Two things from netshift were kept deliberately, because they were right:

1. **The domain must be one the owner can judge without looking anything up.**
   Their own spending qualifies. This is what makes evaluation possible at all,
   and it is why a general-purpose chatbot teaches nothing.
2. **Evals before the first model call.** The common way to fail at entering
   this field is to start from the prompt and improve it by feeling for six
   months.

## Slice 1 -- it runs locally

**Skill:** none new. That is the point -- this is the comfortable part, done
fast so it stops being an excuse.

- [ ] `Transaction`: date (UTC), amount (`decimal`), currency, description,
      category
- [ ] One form to add, one list to see. Nothing else
- [ ] Postgres via EF Core, schema created by a migration rather than by hand
- [ ] `docker compose up` brings the database to healthy

**Done when:** a transaction typed into the form survives a restart of both the
app and the container.

## Slice 2 -- CI

**Skill:** the same discipline as netshift's CI, now with a compiled language
and a container in the loop.

- [ ] GitHub Actions: build, test, on every push
- [ ] Dockerfile for the web app, multi-stage, non-root user
- [ ] Image pushed to `ghcr.io`

Azure Container Registry was the first plan and lost to `ghcr.io`: same job,
but ACR Basic costs around 5 USD a month while GitHub's registry is free for
public repositories. One fewer paid service, one fewer set of credentials.

**Done when:** a fresh clone builds and tests on a machine that is not this one.

## Slice 3 -- deploy

**Skill:** the gap netshift never closed. This is the "CD" the owner asked for.

- [ ] Azure Container Apps, deployed from GitHub Actions, pulling from
      `ghcr.io`

**Why not "all of it in GitHub".** GitHub covers two of the three layers: the
pipeline (Actions) and the registry (`ghcr.io`). It does not cover the third.
Pages serves static files -- HTML, CSS, JS -- and this application needs a live
process and a database beside it. Something has to run the container, and that
is what Azure is here for. Worth knowing the split rather than discovering it
halfway through a deployment.

- [ ] Real configuration and secrets handling -- no connection string in git
- [ ] Migrations applied as a deployment step, not on application startup
- [ ] The URL works from a phone

**On cost:** Azure Database for PostgreSQL is not free. The cheap route while
learning is Postgres as a container next to the app. That is not how production
is run, and the difference should be understood rather than glossed over.

**Done when:** a push to `main` reaches the running site with no manual step.

## Slice 4 -- the AI part, in the right order

**Skill:** the one all of this was for.

1. [ ] **Evals first.** 30-50 hand-labelled transactions with the category they
       should get. Metric and baseline defined **before** the first model call
2. [ ] **A rules baseline.** String matching on the description. Score it.
       This number is what everything later has to beat, and it is often
       embarrassingly hard to beat
3. [ ] A Python service (FastAPI) that categorises a transaction. Called by the
       .NET app over HTTP, with a timeout and a fallback to the rules
4. [ ] An Anthropic adapter behind a port, plus a fake with canned responses so
       tests never hit the network and never cost money
5. [ ] Run the evals. Did the model beat the baseline? Record the number

**Done when:** the improvement over the baseline can be quoted as a number, and
the thing that produced the number can be shown.

## Slice 5 -- operations

- [ ] Redis: identical input must not be billed twice
- [ ] pgvector: find similar past transactions
- [ ] Token and cost accounting per request
- [ ] Graceful degradation: the AI service is down, the app still works
- [ ] Evals run in CI on every PR

## Deliberately not doing

Recorded so they do not creep back in:

- Authentication, roles, an admin panel -- until slice 4 is running
- Multi-currency conversion. Amounts keep their currency; no implicit maths
- Bank integrations. Manual entry and CSV import are enough to learn from
- Anything resembling investment or financial advice. Categorising past
  spending is a data problem; telling someone what to do with their money is
  not what this project is
