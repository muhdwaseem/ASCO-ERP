# ASCO API (`server/Asco.Api`)

Stateless ASP.NET Core 8 JSON API over **C-ERP's unmodified accounting core** (`AegisErp.Domain` +
`AegisErp.Infrastructure`), referenced from the C-ERP checkout — ASCO never edits C-ERP code.

## Run locally

```bash
# 1. API (creates its own asco_dev.db and seeds C-ERP's demo companies/users)
dotnet run --project server/Asco.Api --artifacts-path artifacts      # http://localhost:5080

# 2. Front end (proxies /api → :5080)
npm run dev
```

Sign in with a C-ERP account — locally the demo `owner@aegiserp.com` (password in C-ERP's
`SeedData.DemoAdminPassword`). "Explore with demo data" still works with no API running.

`--artifacts-path artifacts` keeps every build output (including the referenced C-ERP projects)
inside this repo, so building ASCO never writes into the C-ERP folder.

C-ERP location: defaults to `D:\C-ERP\C_ERP-main\src`; override with `-p:CErpSrc=<path>` or the
`CERP_SRC` environment variable (CI / other machines).

## Tests

```bash
dotnet test server/Asco.Api.Tests --artifacts-path artifacts
```

Boots the real API on a throwaway Sqlite DB seeded with C-ERP demo data and checks: 401 (never a
redirect) when signed out, bad-password rejection, CSRF header enforcement, company header
required, unknown company → 403, **every read endpoint → 200 for every company**, trial balance and
balance sheet balanced per company, company data isolation, and a Viewer blocked from payroll/team.

## Design

| Concern | How |
|---|---|
| Auth | ASP.NET Identity cookie (`asco.auth`, HttpOnly, SameSite=Strict) on C-ERP's `AppUser` store — same users as C-ERP. Login rate-limited (10/min/IP) + Identity lockout. |
| Company isolation | Every `/api/*` data call sends `X-Company-Id`. `CompanyScopeFilter` re-checks the grant (`UserCompanyAccess`), blocks suspended subscriptions, then sets C-ERP's scoped `CurrentCompany` — the same object its DbContext query filters read. |
| Permissions | `CanPost` / `CanAdminister` / `CanAccessPayroll` computed exactly as C-ERP's `CompanySession`; payroll + team endpoints gated. |
| CSRF | Non-GET `/api` calls must send `X-ASCO: 1` (cross-site forms can't). |
| Shared database safety | The API **never migrates or seeds** Postgres/SQL Server — C-ERP owns the schema; the API refuses to start if migrations are pending. C-ERP's `InvoiceAutomationHostedService` is removed so recurring invoices aren't generated twice. |
| Scale-out | No per-user server state. Set `DataProtection:KeysPath` to a shared volume (or move keys to the DB/Redis) so every instance can read the auth cookie; put N instances behind a load balancer; `/health` for probes. |
| Output | Flat DTOs / C-ERP read-model records only — never raw entity graphs. Enums as strings. |

## Endpoints (Phase 1 — read)

`/api/auth/login|logout|me` · `/api/dashboard` · `/api/accounts` · `/api/opening-balances` ·
`/api/general-ledger` · `/api/vouchers` · `/api/customers` (+`/{id}/statement`) · `/api/agents` ·
`/api/sales-invoices` · `/api/estimates` · `/api/delivery-notes` · `/api/recurring-invoices` ·
`/api/receipts` · `/api/credit-notes` · `/api/ar-aging` · `/api/outstanding-invoices` ·
`/api/transactions` · `/api/vendors` (+`/{id}/statement`) · `/api/purchase-invoices` ·
`/api/vendor-payments` · `/api/debit-notes` · `/api/expenses` · `/api/ap-aging` ·
`/api/expense-transactions` · `/api/items` · `/api/units` · `/api/item-categories` · `/api/item-kits` ·
`/api/leads` · `/api/fixed-assets` · `/api/employees`* · `/api/payroll-runs`* · `/api/expiring-documents`* ·
`/api/reports/{trial-balance|profit-and-loss|balance-sheet|cash-flow|segments|customer-revenue|vendor-spend|commission|reassignment-audit|vat-control}` ·
`/api/fiscal-periods` · `/api/cost-centers` · `/api/currencies` · `/api/tax-codes` · `/api/service-kits` · `/api/team`†

\* payroll grant · † company admin. Date filters: `?from=&to=` (default: this year to today), `?asOf=`.
