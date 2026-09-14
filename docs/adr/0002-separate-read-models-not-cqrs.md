# ADR-0002 — Separate read models, one database

**Status:** Accepted · 2026-09-11
**Relates to:** Story 3 (availability), Story 4 (sales summary)

## Context

Two of the four stories only read. The write path has a hard requirement — INV-1,
defended by ADR-0001. The read paths have a different one: be fast, and do not get in
the way of selling.

Serving both through one model means the reads pay for the write path's structure, and
the write path is constrained by what the reads want to display.

## Options considered

| Option | Why not |
|---|---|
| One model for both | Reporting drags aggregate loading and change tracking into a path that needs neither |
| Full CQRS with a projected read store | Needs events, a projector and a story for lag, and buys eventual consistency where a strongly consistent answer is already available |
| A read replica | Premature at this size, and introduces replication lag into a number buyers act on |

## Decision

Separate the models, not the storage. The command side loads domain aggregates and
enforces invariants. The query side projects straight to a DTO and never materialises a
domain entity:

```csharp
await db.PricingTiers
    .Where(t => t.EventId == eventId)
    .Select(t => new TierAvailability(t.Id, t.Name, t.Price, t.Remaining))
    .AsNoTracking()
    .ToListAsync(ct);
```

Same database, same transaction boundary, so reads are strongly consistent.

This is CQRS in the sense the pattern originally meant — separate models for reading and
writing — and not in the sense the term is usually used, which is event sourcing plus a
projected store.

## Consequences

- Adding a field to a report does not touch the domain.
- A tier's price is known in two places. That is deliberate: a DTO is a contract with a
  client and an entity is a model of a rule.
- Availability is **advisory**, and the spec says so. Only the purchase path is
  authoritative, because only it holds the row lock.

## What would change this

Reporting volume that measurably competes with selling. The next step is a separate
connection string for the query path pointed at a read replica — which is why the query
side projects rather than loading aggregates, so that becomes configuration rather than
a rewrite.
