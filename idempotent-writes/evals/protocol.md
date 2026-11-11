# Idempotent-write protocol (eval oracle spec, domain-neutral)

Source of truth for the oracles of the `idempotent-writes` and `api-design` evals until the skills exist. Written in terms of a generic *resource*; it must hold for any domain (blogs, orders, tickets, shipments, ...). Blog/Post are only one fixture.

## Vocabulary
- **Resource**: an entity exposed over HTTP. Has a public id (GUID) and a set of fields.
- **Client-writable fields**: fields a client may set via POST or PUT.
- **Server-owned fields**: counters, analytics (views, clicks, downloads), timestamps, derived or aggregated values, internal keys. They are never in the representation hash, in any domain.
- **Representation**: public id + client-writable fields (+ public id of an immutable parent where one exists). Server-owned fields are not part of it.
- **Canonical hash**: `base64url-unpadded(SHA-256(UTF-8(canonical JSON of the representation)))`. Compact JSON, fixed field order, GUIDs lowercase `D` format, strings NFC, escape only `"`, `\` and control characters. The ETag header is the hash in quotes.
- **Creation hash**: the canonical hash of the create request's representation; stored immutably on the row.

## Canonical form details
- Field order: `id`, then the parent id (children), then client-writable fields in declared order.
- Numbers: decimal, no exponent, no trailing zeros (`12.50` and `12.5` hash the same).
- A server-owned field may live on a parent or the resource; changing it never changes any ETag or the representation, and a counter updated on read must not use the ETag concurrency token.

## Identity and data
- Routes, bodies and headers use only the public GUID. Internal int keys never leave the service.
- Public id: unique index (alternate key). Bookkeeping columns (`ETag` as EF concurrency token, immutable `CreationHash`, nullable `DeletedAt` tombstone) are never part of a representation.
- The ETag is a hash of content. Never a row version or sequence number.

## Principles
- A success stays a success on retry (`200` vs `201` is not material).
- `409` and `412` mean the server state genuinely conflicts. They never mean "your request already took effect".

## Operations (order for PUT/DELETE: 400 validation, 428 missing If-Match, 404/410 lookup, ETag logic)
**Create** `POST /{collection}` with the representation including a client-chosen id.
- Insert optimistically (no check-then-insert). On unique violation on the public id, read the row ignoring the tombstone filter and compare `CreationHash` with the hash of the request:
  - equal, live row: `200` with the current representation and current ETag (replay);
  - equal, tombstoned: `410`;
  - different: `409`.
- First success: `201`, `Location`, `ETag`.
- Child resources are created under the parent route; the body parent id must equal the route (`400` otherwise); a missing parent is `404`, a tombstoned parent `410`. The same id under a different parent is `409`.

**Read** `GET /{collection}/{id}`: `200` + ETag; `304` on matching `If-None-Match`; `404` absent; `410` tombstoned.

**Replace** `PUT /{collection}/{id}` full replacement, body id must equal route (`400`), `If-Match` required (`428` when absent).
- Let N be the hash of the intended representation. If current ETag == N, the change is already applied: `204` with ETag N (regardless of `If-Match`).
- A no-op PUT (intended hash equals the current ETag) returns `204` with the unchanged ETag.
- Else if `If-Match` == current ETag (or `*`): update, ETag becomes N, `204`. A concurrency conflict at write time (concurrent writer) is re-evaluated once, then `412`.
- Else `412` with the current ETag header.
- Immutable fields (parent link) differing from stored: `409`.

**Delete** `DELETE /{collection}/{id}`, `If-Match` required (`428`).
- `If-Match` must still be present for an unknown id (`428` otherwise).
- Absent or already tombstoned: `204`, never `404`.
- Live and `If-Match` != current ETag: `412`.
- Else tombstone (compare-and-set on ETag) and tombstone dependants in the same transaction: `204`.

## Errors
`application/problem+json`: 400, 404, 409, 410, 412, 428. Success variants are return values in the service; failures are exceptions mapped at the edge.

## Known limits
Create replay after a later delete is `410` (an intervening request by another client caused it). A replayed create after a later update returns the current state, not the original body.
