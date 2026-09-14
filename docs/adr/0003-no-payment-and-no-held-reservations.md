# ADR-0003 — Sell outright; no payment, no held reservations

**Status:** Accepted · 2026-09-11
**Relates to:** Story 2 (purchase), the out-of-scope table in the spec

## Context

The brief asks for ticket purchase and does not mention payment. But whether money
moves before or after inventory is committed changes the concurrency design, not just
the plumbing — so it is recorded as a decision rather than left as a silence.

## Decision

An order is created and its tickets issued in one transaction. Inventory is committed
at that moment. There is no pending state, no hold, and no expiry.

Nothing about payment is stubbed or half-built. A payment interface with no
implementation behind it is a claim the code cannot keep.

## Why not build the reservation flow anyway

Because it is a different design, not a smaller version of this one.

Payment authorization takes hundreds of milliseconds and can time out. Holding a row
lock across that call would block every other buyer of the tier for the duration of a
third-party network call. So the single conditional `UPDATE` stops being sufficient and
the flow becomes three steps with state between them: reserve, authorize, then confirm
or compensate.

That brings in reservation expiry and a reaper that races the confirm path, plus
reconciliation for authorizations whose outcome is unknown. Each is a correctness
problem of the same class as the oversell. Doing them partially would produce something
that demos and loses money.

## Consequences

- `orders` has no status column. Order existence is the commitment. Adding reservations
  later means adding that column and a state machine; the existing constraints stay
  valid.
- Cancellation and refunds fall out of scope with payment, being the compensating half
  of a state machine that does not exist.
- The concurrency proof stays honest: AC-2.3 tests the real path.

## What would change this

Any requirement that money moves. The first thing to build then is reservation expiry,
because it is the part that silently destroys inventory rather than loudly failing.
