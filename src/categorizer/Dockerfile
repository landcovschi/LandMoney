# The categorizer image -- #39.
#
# Two stages, the same shape as the root Dockerfile of #23: resolve dependencies
# where the tooling lives, run where it does not. The uv binary is copied from
# its own published image rather than installed with a curl-into-sh, so the
# version is a tag that can be read in a diff.
#
# Build context is this folder, NOT the repository root -- see docker-compose.yml.
# That is the opposite of the .NET image, and it is right for the opposite
# reason: the .NET app has to reach src/landmoney.client, while nothing here
# reaches outside src/categorizer. Note the consequence, since it is the one
# thing that will surprise someone later: `evals/` is not in this context and
# must never need to be. The scorer imports the service; the service imports
# nothing of the scorer.

# --- resolve -----------------------------------------------------------------
FROM python:3.13-slim AS build

# Pinned, and read off the registry rather than remembered. `:latest` here would
# make the resolver a moving part of every build, which is the whole thing
# uv.lock exists to stop.
COPY --from=ghcr.io/astral-sh/uv:0.11.3 /uv /usr/local/bin/uv

WORKDIR /app

# Manifests first, source second. Same layer-caching reason as `COPY *.csproj`
# before `COPY .` in a .NET image: editing main.py must not re-resolve the
# dependency graph. README.md is in this COPY and is not documentation here --
# `pyproject.toml` names it in `readme = `, and hatchling refuses to build the
# package without it. Measured: `OSError: Readme file does not exist`.
COPY pyproject.toml uv.lock README.md ./

# --frozen: fail if uv.lock does not match pyproject.toml, rather than silently
# resolving something newer. The container equivalent of `--locked` in CI, and
# the reason the lock file is committed at all.
# --no-dev: pytest and httpx2 are not in the image that ships.
# --no-install-project: the project itself is installed after its source is
# copied, one layer down, so a code change does not invalidate this one.
RUN uv sync --frozen --no-dev --no-install-project

COPY src ./src
RUN uv sync --frozen --no-dev

# --- run ---------------------------------------------------------------------
FROM python:3.13-slim AS final

# Non-root, matching the root Dockerfile's uid 1654. The number is arbitrary and
# being *not* 0 is the point; keeping it equal to the other image's is so that a
# volume shared between them one day does not need two answers.
RUN useradd --uid 1654 --create-home --shell /usr/sbin/nologin app

WORKDIR /app

# The virtual environment carries absolute paths in its scripts, so it is copied
# to the same path it was built at. Moving it is what breaks a .venv -- the same
# failure CLAUDE.md records for moving this repository off the desktop.
COPY --from=build --chown=app:app /app/.venv /app/.venv
COPY --from=build --chown=app:app /app/src /app/src

# Ahead of PATH, so `python` and `uvicorn` mean the ones in the environment.
# This is what `uv run` does for you locally; there is no uv in this image, and
# there is nothing for it to do -- the dependencies are already resolved and the
# lock file is not here.
ENV PATH="/app/.venv/bin:$PATH" \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

# PYTHONUNBUFFERED is not a tidiness setting. Python buffers stdout when it is a
# pipe rather than a terminal, which is exactly what Docker gives it, so without
# this the last few log lines before a crash are lost -- the ones that would say
# why. The .NET image has no equivalent because ILogger writes through and does
# not buffer.

USER app

EXPOSE 8000

# No curl, wget or nc in python:3.13-slim either -- the same bareness CLAUDE.md
# measured in the aspnet image. Unlike that one, this image ships an interpreter
# that can do it, so the healthcheck costs no extra layer. urllib raises on a
# non-2xx status, which is what makes a bare urlopen a sufficient test.
#
# This one is read: docker-compose.yml gates the app's depends_on on it. Azure
# Container Apps would ignore it and probe from outside, which is why the root
# Dockerfile has none.
HEALTHCHECK --interval=5s --timeout=3s --retries=10 --start-period=5s \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=2)"]

# Exec form, so uvicorn is pid 1 and receives SIGTERM directly. The shell form
# puts /bin/sh at pid 1, which does not forward signals, and `docker compose
# down` then waits the full ten seconds before killing the container.
#
# 127.0.0.1 in the healthcheck above and 0.0.0.0 here: the check runs inside this
# container, the traffic arrives from another one.
CMD ["uvicorn", "categorizer.main:app", "--host", "0.0.0.0", "--port", "8000"]
