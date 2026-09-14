# ADR-0004 — Test against real PostgreSQL, never an in-memory provider

**Status:** Accepted · 2026-09-12
**Defends:** the credibility of ADR-0001

## Context

The central claim of this repository is that it cannot oversell. That claim rests on
three things, all properties of PostgreSQL and none of them properties of EF Core:

- row-level locking, and what `READ COMMITTED` re-evaluates once a lock is released
- `CHECK` constraints
- unique-index violations surfacing as SQLSTATE `23505`

The EF Core in-memory provider has none of them. A concurrency test written against it
passes and proves nothing — a green test certifying the one property that matters,
incorrectly.

## Options considered

| Option | Why not |
|---|---|
| EF Core in-memory provider | Cannot express the behaviour under test |
| SQLite in-memory | A different engine with different locking. Green says nothing about PostgreSQL |
| A shared database that CI and developers point at | Tests contend, order-dependent failures appear, and the suite cannot run offline |
| A compose database assumed to be already running | "It failed because you forgot a command" is a poor first experience for a reviewer |

## Decision

[Testcontainers](https://testcontainers.com/) starts a real `postgres:17-alpine`
container per test run, on a random host port, and destroys it afterwards.

The schema is created by running the **migrations** — `MigrateAsync()`, not
`EnsureCreatedAsync()` — so the tests exercise the same migration that will be applied
to production. A schema built from the model would let a defective migration pass CI.

The whole contract for a reviewer is: Docker running, `dotnet test`.

## Consequences

- The suite needs a Docker daemon. That is a real cost and the right one — a suite that
  ran without it would be testing something other than the claim.
- The container is a shared collection fixture; tests truncate between cases rather than
  restarting it.
- The image tag is pinned, so a run next month tests the same engine as today.
- CI needs no database service block. GitHub's `ubuntu-latest` runners have a Docker
  daemon and Testcontainers finds it.
