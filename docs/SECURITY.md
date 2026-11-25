# Security

This maps controls to the OWASP API Security Top 10 (2023) and to each v1 finding in [REVIEW.md](REVIEW.md). Tests are in `tests/Accrue.Tests`.

| OWASP API | Control | v1 finding | Test |
|---|---|---|---|
| **API1** Broken object-level auth | No endpoint accepts a user id; the owner comes from the token's `sub`. Foreign object ids return 404. | #6 | `Users_cannot_redeem_someone_elses_investment` |
| **API2** Broken authentication | Identity PBKDF2 hashing; lockout after 5 tries for 15 min; identical errors and equalised timing for unknown accounts. JWT with issuer, audience, lifetime and an `HS256`-only allow-list (no `alg: none`). 15-minute access tokens; rotating hashed refresh tokens with family revocation on reuse. Signing key from secrets, at least 32 bytes, required in production. | #2, #3, #4 | `Tampered_or_foreign_tokens_are_rejected`, `Passwords_are_hashed_not_stored_in_plain_text`, `Refresh_tokens_rotate_and_reuse_revokes_the_whole_session`, `Login_errors_do_not_reveal_whether_an_account_exists` |
| **API3** Broken object-property auth (mass assignment) | Requests are dedicated DTOs; unknown JSON members are rejected | #5 | `Register_rejects_mass_assignment_of_admin_or_balance` |
| **API4** Unrestricted resource consumption | Rate limits: auth 10/min/IP, money 30/min/user, a global token bucket. 64 KB body cap, JSON depth 16, page size ≤ 100. | #15 | config |
| **API5** Broken function-level auth | Admin routes require the `admin` role claim. Roles are granted server-side only (bootstrap list or admin action). | #8 | `Only_admins_can_manage_plans` |
| **API6** Sensitive business flows | Idempotency keys on every money endpoint; optimistic concurrency on balances; no negative or sub-kobo amounts; balance check before any change; daily accrual exactly once | #7, #9, #12, #13 | `Retried_deposit_with_same_key_is_applied_once`, `Same_key_with_a_different_body_is_rejected`, `Negative_zero_or_sub_kobo_amounts_are_rejected`, `Withdrawals_cannot_overdraw`, `Interest_accrues_daily_exactly_once…`, `The_ledger_always_balances_globally` |
| **API7** SSRF | No outbound calls | none | none |
| **API8** Security misconfiguration | ProblemDetails without stack traces; malformed JSON returns 400, not 500. Headers: `default-src 'none'`, `frame-ancestors 'none'`, nosniff, no-referrer, `no-store`. No `Server` header. HSTS and HTTPS redirect outside Development. OpenAPI only in Development. | #15 | `Security_headers_are_present`, `Malformed_json_is_a_400_not_a_500` |
| **API9** Improper inventory | Versioned routes (`/api/v1`); a single documented surface (README) | #16 | none |
| **API10** Unsafe consumption | No third-party APIs consumed | none | none |

**Supply chain:** the build fails on any NuGet package with a known vulnerability (`NuGetAudit`, level low, including transitive dependencies). Package versions are pinned centrally.

## Operating it

- **Signing key:** set `Jwt__SigningKey` from a secret store (at least 32 random bytes). Rotating it signs everyone out, by design.
- **Data:** back up the `App_Data` volume.
- **Admins:** bootstrap through `Jwt__AdminEmails__0=<you>`, then remove the setting.
- **Proxies:** behind a reverse proxy, configure forwarded headers so rate limits see client IPs.
