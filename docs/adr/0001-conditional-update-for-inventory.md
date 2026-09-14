# ADR-0001 — Decrement inventory with a conditional UPDATE

**Status:** Accepted · 2026-09-11
**Defends:** INV-1 — tickets issued for a tier never exceed its allocation

## Context

Two buyers can ask for the last ticket at the same moment, against any number of API
instances. The obvious implementation has a gap between the check and the write:

```csharp
var tier = await db.PricingTiers.FindAsync(tierId);   // both read remaining = 1
if (tier.Remaining >= quantity)                       // both pass
{
    tier.Remaining -= quantity;                       // both write 0
    await db.SaveChangesAsync();                      // two tickets, one seat
}
```

Nothing is wrong with either statement. The defect is the gap between them, and it is
invisible in every single-threaded test.

## Options considered

| Option | Why not |
|---|---|
| `SERIALIZABLE` isolation | Correct, but pushes serialization failures and a retry loop onto every caller |
| `SELECT … FOR UPDATE` | Correct, but two round trips and a lock held across application think-time |
| Optimistic concurrency on the tier | Correct, but under a stampede for the last ticket, retries are the common path |
| Distributed lock (Redis) | A second system to run to solve what one SQL statement already solves |

## Decision

One statement does the check and the write together:

```sql
UPDATE pricing_tiers
   SET remaining = remaining - @quantity
 WHERE id = @tierId
   AND remaining >= @quantity;
```

Expressed as `ExecuteUpdateAsync` with the predicate in the `Where`. There is no
read-then-write, so there is no window. **Rows affected of zero is the sold-out
answer**, and the branch that returns 409.

This is safe under PostgreSQL's default `READ COMMITTED` because a statement that
blocks on a row lock re-evaluates its `WHERE` clause against the newly committed row
once the lock is released. The loser does not act on the value it read before waiting.

A `CHECK (remaining >= 0)` constraint backs it up, so the oversold state cannot exist
in the table whatever code runs against it.

`PricingTier` exposes no `Decrement()` method. The only path that changes `remaining`
is the statement above.

## Consequences

- The decrement bypasses the change tracker, so the tier is stale afterwards. The
  purchase slice loads it `AsNoTracking` to make that explicit.
- The decrement and the order insert are two separate database operations, so they need
  an explicit transaction — EF's automatic one covers `SaveChanges` alone.
- A rule this important cannot be proven by unit tests. `AC_2_3` drives concurrent
  purchases against real PostgreSQL and asserts ticket rows, not just the counter. See
  ADR-0004.

## What would change this

Seat selection: inventory stops being a counter and becomes a set of rows. The locking
strategy survives; the statement does not.
