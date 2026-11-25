# Accrue

**A smart-wallet and micro-investment API, built on a double-entry ledger.**
*(Formerly FinEdge. See [docs/REVIEW.md](docs/REVIEW.md) for why it was rebuilt.)*

.NET 10 · Minimal APIs · EF Core · ASP.NET Identity · JWT + rotating refresh tokens · 31 tests

---

## What it does

| | |
|---|---|
| **Accounts** | Register and sign in. Short-lived JWT access tokens (15 min) and rotating refresh tokens (30 days) with theft detection. |
| **Wallet** | Deposits and withdrawals (simulated funding rails), a balance, and keyset-paginated transaction history. |
| **P2P transfers** | Send to any user by email, with a note. Both histories show it instantly. |
| **Investments** | Plans with an annual rate, minimum and lock period. Interest accrues **daily**, exactly once per day, and redemption returns principal plus interest. |
| **Dashboard** | Net worth, invested, interest earned, projected monthly interest and 30-day in/out. Cached per user and invalidated on every movement. |
| **Admin** | Create and edit plans, open or close them, and trigger accrual. Requires the `admin` role. |

## Design in one minute

- **Double-entry ledger.** Every movement is a journal entry whose postings sum to zero. Balances are a cached sum guarded by an optimistic concurrency token, so money is never created, destroyed or double-spent. A test proves the whole ledger sums to zero.
- **Idempotent money endpoints.** Every `POST` that moves money requires an `Idempotency-Key`. A retry returns the original response and never charges twice; reusing a key for a different request gets a 422.
- **Owner from the token, always.** No endpoint takes a user id. Someone else's investment id is a 404 (no BOLA).
- **Daily accrual, idempotent by construction.** A unique `(investment, day)` key plus an in-order `AccruedThrough` cursor means the job can run hourly, at startup or manually and still pay each day exactly once.

Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · Security: [docs/SECURITY.md](docs/SECURITY.md)

## Run it

```powershell
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>" --project src/Accrue.Api
```
```powershell
dotnet run --project src/Accrue.Api --launch-profile http
```

- The API listens on `http://localhost:5090`. The OpenAPI document is at `/openapi/v1.json` (Development only).
- In Development an ephemeral signing key is generated if you skip the first step, so tokens reset on restart. **Outside Development the app refuses to start without a 32-byte key.**
- To bootstrap an admin, add your email to `Jwt:AdminEmails` before registering.

```powershell
dotnet test
```

```powershell
docker build -t accrue . ; docker run -p 8080:8080 -e Jwt__SigningKey=<secret> -v accrue-data:/app/App_Data accrue
```

## API

All routes are under `/api/v1`. Send `Authorization: Bearer <accessToken>`. Money `POST`s also need `Idempotency-Key: <8–64 chars>`.

| Method | Route | Notes |
|---|---|---|
| POST | `/auth/register` · `/auth/login` | → `{ accessToken, refreshToken, user }` |
| POST | `/auth/refresh` · `/auth/logout` | `{ refreshToken }`; refresh rotates the token |
| GET | `/me` · `/dashboard` | |
| GET | `/wallet` · `/wallet/transactions?before=&limit=` | |
| POST 💸 | `/wallet/deposits` · `/wallet/withdrawals` | `{ amount, reference? }` |
| POST 💸 | `/transfers` | `{ toEmail, amount, note? }` |
| GET | `/plans` · `/investments` | |
| POST 💸 | `/investments` · `/investments/{id}/redeem` | `{ planId, amount }` |
| GET/POST/PUT | `/admin/plans[/{id}]` | admin |
| POST | `/admin/accrual/run` | admin, `{ through? }` |

Errors are RFC 9457 ProblemDetails, with field-level `errors` for validation failures.

## Project layout

```
src/
  Accrue.Domain          Ledger (Account, JournalEntry, Posting), InvestmentPlan, Investment, InterestAccrual. No dependencies.
  Accrue.Application     Use cases: WalletService, InvestmentService, AccrualService, DashboardService; contracts; ports
  Accrue.Infrastructure  EF Core (SQLite), Identity, JWT/refresh tokens, cache, accrual worker
  Accrue.Api             Endpoints, idempotency filter, auth, rate limits, headers
tests/Accrue.Tests       Domain + HTTP-level security and money-integrity tests
docs/                    REVIEW (v1 audit) · ARCHITECTURE · SECURITY
```
