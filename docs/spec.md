# Event Ticketing API

Sell tickets for events without ever overselling, and report on sales.

## Out of scope

Deliberate omissions, not oversights. Each names what would replace it.

| Omitted | Why | What I would build |
|---|---|---|
| Payment provider | Not in the brief; would consume the budget on plumbing | Reserve → authorize → confirm, with compensation (ADR-0003) |
| Authentication | Not in the brief | OIDC bearer tokens; buyer identity from the `sub` claim. Until then the order id is a capability: unguessable by construction, which is why identifiers are UUIDs and not sequential integers |
| Multi-tenancy | No actor in the brief owns an event, and a tenant derived from an unauthenticated header is not isolation | `organiser_id` on `events`, denormalised onto `orders` and `tickets` so policies need no joins; `FORCE ROW LEVEL SECURITY`; tenant set per transaction with `SET LOCAL`; absent context resolves to the nil UUID so it fails closed |
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
| AC-1.7 | Given a date and a time supplied as separate fields with a venue zone, when I GET the event, then the same local date and time are returned alongside the UTC instant. | `AC_1_7_LocalDateAndTimeRoundTrip` |

Related invariants: INV-4, INV-6.

### Fields

The brief names seven fields. This is how each is stored.

| Brief | Stored as | Note |
|---|---|---|
| name | `Name` | |
| description | `Description` | Optional; may be empty, never null |
| venue | `Venue` value object — `Name` + `TimeZoneId` | No identity of its own, so it is owned by the event and mapped to columns on the same table |
| date | part of `StartsAtUtc` | Accepted and returned as a separate `date` field; combined with `time` and the venue zone on write |
| time | part of `StartsAtUtc` | As above |
| total ticket capacity | `TotalCapacity` | Ceiling for INV-4 |
| pricing tiers | `PricingTier` rows | Each with a price, an allocation, and a remaining count |

Date and time are two fields at the API boundary because the brief says so, and one
instant in storage because two local parts cannot be ordered or compared across
zones. The venue's IANA zone is what joins them.

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
| AC-2.10 | Given an order id that does not exist, when I GET it, then the response is 404 with a Problem Details body. | `AC_2_10_UnknownOrderReturns404` |

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
| GET | `/v1/orders/{orderId}` | 200 | 404 |

`POST /v1/events/{id}/orders` requires an `Idempotency-Key` header.

Order creation is nested under the event because creation is scoped to it. Order
retrieval is flat, because the order id is globally unique and self-sufficient —
requiring the event id too would force the caller to hold two identifiers and buy
nothing, since the event id is not a second secret. The `Location` header returned
by `POST` points at the flat URL.

With no authentication in scope, **possession of the order id is the authorization**
to read the order — a capability URL, in the manner of an airline booking reference.
That is only sound because the id cannot be guessed, which is the load-bearing reason
identifiers are UUIDs here. The known weakness is that URLs leak — browser history,
`Referer`, access logs — so with authentication the order would be scoped to the
buyer's `sub` claim and the id demoted to a convenience.

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
- The full suite passes from a clean clone via `dotnet test` alone, with Docker
  running and nothing else set up
- Any decision worth arguing about is recorded as an ADR
- No production credential appears in any commit. The local development password is a
  deliberate exception, committed in `docker-compose.yml` — where a comment explains
  why it is not a secret — and in `appsettings.Development.json`, which points at that
  same container

## Amendments

This spec changes when implementation proves it wrong. Amendments are committed
on their own, with the reason in the commit message.

| Date | Change | Reason |
|---|---|---|
| 2026-09-11 | Initial | — |
| 2026-09-12 | Added `GET /v1/orders/{orderId}`, AC-2.10, AC-2.11 | `POST` returned a `Location` header pointing at no endpoint |
| 2026-09-12 | Recorded multi-tenancy as out of scope | Absent from the brief; an unauthenticated tenant header is not isolation, and silence read as an oversight |
| 2026-09-12 | Added event `Description` and a `Venue` value object; AC-1.7 | The brief names description and venue as fields; the model had neither |
| 2026-09-13 | Removed AC-2.9 | It asserted the behaviour of a payment authorizer that is out of scope, so it could never have a test while the definition of done requires one for every AC |
| 2026-09-13 | Definition of done: the suite runs on `dotnet test` alone, not `docker compose up` first | Testcontainers starts its own database, so the compose stack was never on the test path and the line described something that had stopped being true |
| 2026-09-13 | Definition of done: "no connection string" narrowed to "no production credential" | The rule as written was already broken by the local password in `docker-compose.yml`, and hiding the same value in user secrets cost a setup step while protecting a throwaway container bound to localhost |
