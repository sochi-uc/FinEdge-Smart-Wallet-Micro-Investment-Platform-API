# Review of FinEdge v2.1 — and why it was rebuilt as Accrue

Reviewed: `master` at `e0d91d0` ("Version 2.1 Release"), with 6 C# files and about 200 lines.

**Verdict:** the project did not compile, and even with the compile error fixed it would have been unsafe to run with real or demo money. None of the advertised features beyond deposit and invest existed. Rather than patch it, it was rebuilt from first principles as **Accrue**; see the README and [ARCHITECTURE.md](ARCHITECTURE.md).

## Findings

Severity: 🔴 critical · 🟠 high · 🟡 medium.

| # | Sev | Finding | Where | Impact |
|---|---|---|---|---|
| 1 | 🔴 | **Does not compile.** The JWT key line in `Program.cs` contains an unterminated string literal. | `Program.cs` | Nothing runs. |
| 2 | 🔴 | **Two different hard-coded JWT keys.** Validation uses one, `AuthController` signs with `"SUPER_SECRET_FINEDGE_KEY_12345"`. Both are committed to source. | `Program.cs`, `AuthController.cs` | Every token fails validation; anyone with the repo can forge tokens once the keys match. |
| 3 | 🔴 | **Tokens contain no claims and never expire.** There is no `sub`, `exp`, issuer or audience, and issuer/audience validation is disabled. | `AuthController.Login` | A token identifies nobody and lives forever. |
| 4 | 🔴 | **Plain-text passwords.** They are stored as given and compared with `==` in a LINQ query. | `AuthController`, `User.Password` | A database leak exposes every password, which is also reused on other sites. |
| 5 | 🔴 | **Mass assignment on register.** The endpoint binds the full `User` entity, so a caller can send `"isAdmin": true, "wallet": {"balance": 1000000000}`. | `AuthController.Register` | Self-made admins and free money. |
| 6 | 🔴 | **BOLA/IDOR.** `POST /api/wallet/deposit/{userId}/{amount}` and `Invest(i.UserId)` trust the user id from the client. | `WalletController`, `InvestmentController` | Any user can move any other user's money. |
| 7 | 🔴 | **Negative amounts accepted.** A deposit of `-500` withdraws, and investing `-1000` mints money. | both controllers | The ledger can be drained or inflated at will. |
| 8 | 🔴 | **No admin check on `POST /api/invest/plan`.** | `InvestmentController` | Any user creates a 10,000% ROI plan. |
| 9 | 🟠 | **The earnings job pays the full ROI every minute,** compounding, with no record of what was paid. It also never calls `SaveChanges`, so in practice it silently does nothing. | `InvestmentEarningsService` | Correct either never runs or pays ~525,600× per year. |
| 10 | 🟠 | **Wallet not loaded.** `Users.Find(id)` doesn't `Include(Wallet)`, so `u.Wallet` is a fresh empty object and updates are lost or misattributed. `Find(id)!` returns a 500 for unknown ids. | controllers | Balances are wrong; unknown ids crash. |
| 11 | 🟠 | **No ledger or audit trail.** The `Transaction` table exists but is never written; balances are a mutable number. | everywhere | Money can't be reconciled or explained. |
| 12 | 🟠 | **Race conditions and double-spend.** There are no database transactions and no concurrency control, so two concurrent debits both pass the balance check. | controllers | Overdrafts. |
| 13 | 🟠 | **No idempotency.** A retried request (mobile network drop) deposits or invests twice. | money endpoints | Duplicate charges. |
| 14 | 🟡 | **In-memory EF provider.** All data is lost on restart, and it doesn't enforce relational constraints. | `Program.cs` | Not a real store. |
| 15 | 🟡 | **Swagger in every environment,** no HTTPS/HSTS, no rate limiting, no validation, no ProblemDetails, synchronous EF calls. | `Program.cs` | Attack surface; brute force; thread starvation. |
| 16 | 🟡 | **Advertised features missing:** withdrawals, P2P transfers, history, analytics, caching, admin user management. | README | The README overstated the product. |
| 17 | 🟡 | **.NET 8** is out of support from November 2026. | csproj | No security patches. |

## Disposition

Every finding is addressed by design in Accrue, and each critical finding has a regression test. See the table in [SECURITY.md](SECURITY.md).
