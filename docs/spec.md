# Event Ticketing API

Sell tickets for events without ever overselling, and report on sales.

## Out of scope

Deliberate omissions, not oversights. Each names what would replace it.

| Omitted | Why | What I would build |
|---|---|---|
| Payment provider | Not in the brief; would consume the budget on plumbing | Reserve → authorize → confirm, with compensation (ADR-0003) |
| Authentication | Not in the brief | OIDC bearer tokens; buyer identity from the `sub` claim |
| Seat selection | Changes inventory from a counter to a set | Per-seat rows under the same locking strategy |
| Refunds and cancellation | Not in the brief | Compensating order state machine, restoring inventory |
| Multi-currency | One currency per event is sufficient here | Minor units plus ISO 4217 code, never summed across currencies |
| Read replica for reporting | Premature at this size | Separate connection string for the query path (ADR-0002) |
| Distributed tracing backend | Instrumentation is present; no collector is run | OTLP export to a collector |

## Invariants

These hold regardless of implementation, load, or number of running instances.
The design is answerable to them.

| # | Invariant |
|---|---|
| INV-1 | Tickets issued for a pricing tier never exceed that tier's allocation, under any level of concurrency and any number of API instances. |
| INV-2 | A purchase replayed with the same idempotency key and the same payload returns the original result and issues no additional tickets. |
| INV-3 | Order totals are computed from stored tier prices. A client-supplied amount is never trusted. |
| INV-4 | The sum of an event's tier allocations never exceeds its total capacity. |
| INV-5 | Money is stored and computed as `decimal`. Floating point is never used for money. |
| INV-6 | Event times are stored as a UTC instant plus the venue's IANA time zone id. A naive local time is never stored. |

INV-1 is what this exercise is about. It is defended twice: in application logic,
and by a database `CHECK` constraint that makes the violating state
unrepresentable. See ADR-0001.

## Story 1 — Manage events

As an organiser, I want to create and maintain events with pricing tiers, so that
tickets can be sold against them.

| AC | Given / When / Then | Test |
|---|---|---|
| AC-1.1 | Given a valid event with tiers, when I POST it, then it is created and the response is 201 with a `Location` header. | `AC_1_1_CreateEventReturns201WithLocation` |
| AC-1.2 | Given tier allocations summing above the event's total capacity, when I POST it, then the request fails with 422 and nothing is created. | `AC_1_2_TierAllocationsCannotExceedCapacity` |
| AC-1.3 | Given an event id that does not exist, when I GET it, then the response is 404 with a Problem Details body. | `AC_1_3_UnknownEventReturns404` |
| AC-1.4 | Given an event I have already read, when I PUT it with a stale `If-Match`, then the response is 412 and the event is unchanged. | `AC_1_4_StaleIfMatchReturns412` |
| AC-1.5 | Given an event with tickets already sold, when I DELETE it, then the response is 409 and the event remains. | `AC_1_5_CannotDeleteEventWithSales` |
| AC-1.6 | Given an event created with a venue time zone, when I GET it, then the response carries both the UTC instant and the IANA zone id. | `AC_1_6_EventTimeIsUtcPlusZone` |

Related invariants: INV-4, INV-6.

## Story 2 — Purchase tickets

As a buyer, I want to purchase tickets for an event, so that I hold a confirmed
place at it.

| AC | Given / When / Then | Test |
|---|---|---|
| AC-2.1 | Given a tier with 10 remaining, when I buy 3, then 3 tickets are issued and 7 remain. | `AC_2_1_PurchaseReducesRemaining` |
| AC-2.2 | Given a tier with 2 remaining, when I buy 5, then the request fails with 409 and 2 still remain. Partial fulfilment is never performed. | `AC_2_2_PartialFulfilmentIsRejected` |
| AC-2.3 | Given a tier with 50 remaining, when 200 requests each buy 1 concurrently, then exactly 50 succeed, 150 return 409, and remaining is 0. | `AC_2_3_ConcurrentPurchasesNeverOversell` |
| AC-2.4 | Given a completed purchase, when the same idempotency key and payload are replayed, then the original order is returned and no new tickets are issued. | `AC_2_4_ReplayReturnsOriginalOrder` |
| AC-2.5 | Given a used idempotency key, when it is reused with a different payload, then the request fails with 409. | `AC_2_5_KeyReuseWithDifferentPayloadConflicts` |
| AC-2.6 | Given a request carrying its own price, when I purchase, then the client price is ignored and the total is computed from stored tier prices. | `AC_2_6_ClientSuppliedPriceIsIgnored` |
| AC-2.7 | Given an event whose start time has passed, when I purchase, then the request fails with 422. | `AC_2_7_CannotPurchasePastEvent` |
| AC-2.8 | Given a quantity of zero or negative, when I purchase, then the request fails with 422. | `AC_2_8_InvalidQuantityRejected` |
| AC-2.9 | Given the payment authorizer declines, when I purchase, then no tickets are issued and remaining is unchanged. | `AC_2_9_DeclinedPaymentIssuesNoTickets` |

Related invariants: INV-1, INV-2, INV-3.

**AC-2.3 is the centrepiece.** It is the only test that proves INV-1, and it is
the test that fails if the locking strategy is wrong.

Purchase is all-or-nothing. A request for 5 against 3 remaining is rejected
rather than partially filled, because partial fulfilment would make the
idempotent replay in INV-2 ambiguous.

## Story 3 — View availability

As a buyer, I want to see what is still available, so that I know whether to
attempt a purchase.

| AC | Given / When / Then | Test |
|---|---|---|
| AC-3.1 | Given an event with tiers, when I GET availability, then each tier's remaining count is returned. | `AC_3_1_AvailabilityReturnsPerTierRemaining` |
| AC-3.2 | Given a sold-out tier, when I GET availability, then it reports zero remaining rather than being omitted. | `AC_3_2_SoldOutTierReportsZero` |

Availability is advisory. A non-zero reading is not a reservation, and the
purchase path is the only authority on whether a ticket can be issued.

## Story 4 — Sales summary

As an organiser, I want a summary of sales for an event, so that I can see how it
is performing.

| AC | Given / When / Then | Test |
|---|---|---|
| AC-4.1 | Given orders across several tiers, when I GET the summary, then tickets sold, remaining, and revenue are returned per tier and in total. | `AC_4_1_SummaryAggregatesByTier` |
| AC-4.2 | Given no sales, when I GET the summary, then zeroed figures are returned rather than 404. | `AC_4_2_SummaryWithNoSalesReturnsZeroes` |
| AC-4.3 | Given mixed tier prices, when I GET the summary, then revenue is exact to the cent. | `AC_4_3_RevenueIsExactDecimal` |

Related invariant: INV-5.

The summary is a read model. It is projected directly to a DTO with a single
aggregate query and never loads domain entities. It tolerates brief staleness;
the purchase path does not. See ADR-0002.

## Error taxonomy

One shape for every error: Problem Details, RFC 9457. Every response carries a
trace id, and the same id appears in the logs.

| Status | Meaning here |
|---|---|
| 400 | Malformed — unparseable body, wrong types |
| 404 | Event, tier, or order does not exist |
| 409 | Conflict with current state — insufficient inventory, delete with sales, idempotency key reused with a different payload |
| 412 | `If-Match` precondition failed on update |
| 422 | Well-formed but semantically invalid — past event, non-positive quantity, allocations exceeding capacity |
| 429 | Rate limited |
| 500 | Unexpected. Never leaks internal detail; correlates by trace id |

## Endpoints

| Method | Route | Success | Notable failures |
|---|---|---|---|
| POST | `/v1/events` | 201 + `Location` | 422 |
| GET | `/v1/events` | 200 | — |
| GET | `/v1/events/{id}` | 200 | 404 |
| PUT | `/v1/events/{id}` | 200 | 404, 412, 422 |
| DELETE | `/v1/events/{id}` | 204 | 404, 409 |
| GET | `/v1/events/{id}/availability` | 200 | 404 |
| POST | `/v1/events/{id}/orders` | 201 + `Location` | 404, 409, 422 |
| GET | `/v1/events/{id}/sales-summary` | 200 | 404 |

`POST /v1/events/{id}/orders` requires an `Idempotency-Key` header.

## Test strategy

| Layer | Covers | Runs against |
|---|---|---|
| Unit | Domain invariants in isolation | Nothing — no I/O |
| Integration | Every AC above, end to end over HTTP | Real PostgreSQL via Testcontainers |
| Concurrency | AC-2.3 | Real PostgreSQL, 200 parallel requests |

No in-memory database provider anywhere. The behaviour under test is concurrent
write behaviour, and an in-memory provider does not have it. See ADR-0004.

## Definition of done

Applies to every story.

- Every AC has a test whose name carries its id, and CI fails if one does not
- `dotnet build` produces no warnings — warnings are errors
- The full suite passes from a clean clone via `docker compose up` and `dotnet test`
- Any decision worth arguing about is recorded as an ADR
- No secret, key, or connection string appears in any commit

## Amendments

This spec changes when implementation proves it wrong. Amendments are committed
on their own, with the reason in the commit message.

| Date | Change | Reason |
|---|---|---|
| 2026-09-11 | Initial | — |
