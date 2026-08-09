# Discord collaboration backend

Status: Accepted

## Decision

Discord is a projection and interest-capture surface for a canonical Trade company. It does not own Trade orders, choose crafters, or advance work automatically.

The backend binds one active Discord installation to a stable Trade `CompanyId`, posts an ownership-bound immutable commission brief through a durable outbox, records Volunteer clicks by stable Discord user ID, and requires an authenticated company operator to select a canonical crafter before assignment.

## Authority boundaries

- `ITradeCompanyService` remains authoritative for company access, order and crafter records, optimistic revisions, and assignment mutations.
- The commission brief store remains authoritative for immutable published terms and persists the canonical company, order, and source order revision attached to those terms.
- The Discord collaboration store owns installation bindings, message identities, interest claims, stable roster identity bindings, reconciliation cursors, and outbox delivery state.
- Discord display names are snapshots for operator context. They are never identity keys and never select a Trade crafter.

Build 1 exposes a single-record company mutation rather than a cross-database transaction. Claim acceptance therefore uses a durable saga: the claim enters `AssignmentPending`, the complete assignment/status/history payload is committed as one revision-guarded Trade order mutation, and the claim becomes `Accepted` only after that mutation is applied or replayed. A crash can leave the claim temporarily pending after the order succeeds, but replay uses the same idempotency key and repairs that state without assigning twice.

## HTTP integration

The existing signed endpoint remains:

- `POST /discord/interactions`

Company-operator integration is exposed under:

- `GET /trade/v1/companies/{companyId}/discord/claims`
- `POST /trade/v1/companies/{companyId}/discord/publications/{publicId}/post`
- `POST /trade/v1/companies/{companyId}/discord/claims/{claimId}/accept`
- `POST /trade/v1/companies/{companyId}/discord/claims/{claimId}/decline`
- `POST /trade/v1/companies/{companyId}/discord/reconcile`

These routes fail closed until the host supplies an `IDiscordCompanyAccessResolver` that returns a canonical `TradeCompanyAccessContext`. Route or body identifiers never manufacture access.

The host must also register its canonical `ITradeCompanyService`. `AddDiscordCollaboration()`, `AddDiscordCompanyAdapters()`, and `MapDiscordCollaborationEndpoints()` are the narrow composition hooks; provisioning supplies verified bindings through `IDiscordInstallationBindingWriter`. No Trade UI or browser database dependency is required.

## Discord security

Interaction requests retain the raw-body size limit, timestamp window, and Ed25519 verification before JSON parsing or domain work. Component clicks must match the configured application and the persisted installation, guild, channel, message, and opaque action token before a claim is recorded.

Runtime Discord access is deliberately smaller than provisioning access. The runtime client can only create, edit, or read messages in the exact configured channel, requires View Channel, Send Messages, and Embed Links in the persisted permission snapshot, and has no channel-management operation. Every outbound message and interaction response sets `allowed_mentions.parse` to an empty array.

The runtime credential is server-only configuration. It is never stored in the collaboration database or returned through the APIs.

## Delivery and reconciliation

Publication, message projection, and outbox enqueue are idempotent. Discord message IDs are persisted after successful creation; later assignment, closure, deletion, or revocation edits that exact message and removes Volunteer.

The outbox leases work durably and retries only bounded transient outcomes. Authorization, permission, missing-message, and invalid-payload responses are terminal. An ambiguous create timeout is not retried because that could duplicate a post; it enters `ReconciliationRequired` and cannot create another message until an explicit recovery proves the original outcome.

Canonical order changes drive lifecycle projection through a persisted company revision cursor. Cursor advancement can commit in the same local transaction as the final projection enqueue. Repricing does not republish immutable terms.

## Deliberate exclusions

- A Volunteer click never assigns an order.
- Discord usernames never auto-bind or auto-create crafters.
- There is no Gateway connection, passive message reading, DM workflow, role management, payment mutation, delivery mutation, or order completion.
- Installation provisioning and the Trade browser UI are separate integration concerns.
