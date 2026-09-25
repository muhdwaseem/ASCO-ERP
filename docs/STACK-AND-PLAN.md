# ASCO — stack decision & build plan

Goal: an accounting product with **every module of C-ERP (Aegis ERP)**, an **Excel-style UI**
(dark ribbon, name box + formula bar, grid, sheet tabs, status-bar Sum/Count/Average), and room
for **AI features** later.

## 1. What C-ERP already is (inventoried from `D:\C-ERP\C_ERP-main`)

- ASP.NET Core 8 · Blazor Server · MudBlazor · EF Core · ASP.NET Identity · PostgreSQL (Supabase) / SQLite dev.
- 3 layers: `AegisErp.Domain` (41 entities), `AegisErp.Infrastructure` (~50 services incl. the
  `JournalPoster` posting engine, advisory-lock numbering, row-lock allocation), `AegisErp.Web` (Blazor UI).
- **545 passing tests** and it is **live in production** for a real client.
- Already has AI plumbing: `AiQueryService`, `BillScanningService`, `QueryLog`, `tools/AiQueryEval`.
- Only functional gap: inventory / stock tracking.

## 2. Recommendation

| Layer | Choice | Why |
|---|---|---|
| **Accounting core** | **Keep C-ERP's .NET Domain + Infrastructure as-is** | The hard part of accounting software is the posting engine, concurrency, multi-company isolation and tests — C-ERP already has all of it, proven in production. Rewriting it = months of work to reach where you already are. |
| **API** | **New `AegisErp.Api` project** (ASP.NET Core Minimal APIs + OpenAPI) in the same solution, calling the existing services | Blazor Server can't give you an Excel-grade grid/ribbon; a JSON API lets any front end sit on the same books. Reuse Identity cookies + `CompanySession` per-company grants. |
| **Front end** | **React 19 + TypeScript + Vite** (this repo) | Best ecosystem for a spreadsheet-like UI; the shell here is already built. Add TanStack Query for server state and generate the TS client from OpenAPI. |
| **Grid** | Custom grid (this repo) now → **Glide Data Grid** (MIT, canvas) when sheets exceed a few thousand rows | Glide gives Excel-style range selection, copy/paste and virtualized rendering for free; AG Grid Community is the alternative (range selection is Enterprise-only). |
| **Database** | PostgreSQL (keep Supabase) | Unchanged — no data migration. |
| **Desktop (optional)** | Wrap the same React app with **Tauri** | If clients want a "real Excel-like desktop app"; one codebase for web + desktop. |
| **AI** | **Claude API** (Anthropic .NET SDK) with tool-use over the read-model services | Tools = trial balance, aging, GL search, document lookup — all scoped to the active company. AI **drafts**, a user with `CanPost` approves, the normal engine posts. Log every call (C-ERP `QueryLog`). |
| **Hosting** | Same as C-ERP: Render (Docker) + Supabase; front end served from `wwwroot` or Vercel | Nothing new to operate. |

**Alternative if you want one language end-to-end:** TypeScript full stack (Fastify/NestJS + Prisma +
Postgres — the CRMgold stack). Only choose this if you're willing to re-implement and re-test the
posting engine, numbering locks, allocation locks and multi-tenant filters; the TypeScript port in
`src/engine` is a starting point but is **demo-grade**, not production-grade.

### Python backend option

Python is a good fit for accounting software — the two biggest open-source ERPs (Odoo, ERPNext) are Python — and it is the strongest language for the AI work planned in Phase 4.

| Choice | Pick | Notes |
|---|---|---|
| Framework | **Django 5 + Django REST Framework** (or Django Ninja) | Built-in auth, permissions, admin screens, ORM + migrations, `transaction.atomic`, `select_for_update()` row locks — exactly what the posting engine needs. Prefer it over FastAPI here: FastAPI means assembling auth, admin and migrations yourself. |
| Database | PostgreSQL (Supabase) | Same as today. |
| Money | `Decimal` fields everywhere (never float) | |
| Background jobs | Celery or Django-Q + Redis | Recurring invoices, depreciation runs, emails, AI bill scanning. |
| Tests | pytest + pytest-django | Port C-ERP's 545 tests as the acceptance suite. |
| AI | `anthropic` Python SDK | Tool-use over report queries, bill-scan PDF/image extraction, pandas for analysis. |
| Front end | **Unchanged — this React app** | Python can't render an Excel-grade UI in the browser; the React shell talks to the Django API. |

**Cost:** everything in C-ERP's .NET layers must be re-written in Python — posting engine, numbering locks, allocation locks, multi-company isolation, HR/payroll/WPS, depreciation, reports — and re-proven with tests. Roughly **+6–10 weeks** vs. keeping the .NET core, and the live client stays on C-ERP until ASCO reaches parity.

**Middle path:** keep the .NET accounting core and add a **Python AI service** (FastAPI + `anthropic`) that the .NET API calls for Ask AI / bill scanning / insights. You get Python where it's strongest without rewriting the ledger.

### How the established products are built

| Product | Deployment | Back end | Database | Customisation | Users / scale model |
|---|---|---|---|---|---|
| **TallyPrime** | Windows desktop (+ remote access) | Native C/C++ engine | Tally's own proprietary data files | TDL (Tally Definition Language) | Gold licence = unlimited users **on the LAN**, limited remote users |
| **Zoho Books** | Cloud SaaS only | Java on Linux, Zoho-owned data centres | PostgreSQL among others (Zoho-run) | Deluge scripting, custom fields, APIs | Multi-tenant cloud; users priced per plan |
| **Odoo** | Cloud (Odoo.sh / Online) or self-hosted | Python, Odoo's own ORM & framework | PostgreSQL | Python modules + XML views, OWL (JS) front end | Stateless workers behind a load balancer; thousands of users per DB |
| **MS Dynamics 365 Business Central** | Cloud (Azure) or on-prem | .NET server tier, AL language | SQL Server / Azure SQL | AL extensions (AppSource) | Azure-scaled multi-tenant service |
| **ASCO (planned)** | Cloud SaaS (+ optional desktop via Tauri) | .NET 8 (C-ERP core) | PostgreSQL | Custom fields & tags → plug-in modules later | Stateless API + React, horizontally scaled |

**Which to follow:** architecture like **Zoho / Dynamics** (cloud, multi-tenant, stateless API),
tech like **Dynamics** (.NET + SQL database — i.e. the C-ERP core you already have), modular
"apps" idea from **Odoo**, keyboard-speed data entry from **Tally**. Language is not what makes
these products scale — Odoo (Python), Zoho (Java) and Dynamics (.NET) all serve huge user counts.
The architecture does.

## 2b. "Unlimited users" — how ASCO scales

No system is literally unlimited; the goal is **adding users only requires adding servers, never
rewriting code**. Rules:

1. **Stateless API servers** behind a load balancer — any request can hit any server; add more as load grows.
   *This is why ASCO moves off Blazor Server:* Blazor Server keeps a live connection + memory per
   user on one server (C-ERP today runs on a 512 MB / 0.5 CPU Render instance), which caps concurrent users.
2. **PostgreSQL + connection pooler (PgBouncer / Supabase pooler)** so thousands of users share a few hundred DB connections.
3. **Read replicas** for reports/dashboards so heavy reports never slow down invoice posting.
4. **Multi-tenant isolation** by `CompanyId` (already in C-ERP) + Postgres row-level security; very large tenants can later move to their own database/shard.
5. **Background jobs** (recurring invoices, depreciation, email, AI) on a queue, not in web requests.
6. **Redis cache** for sessions, permissions and hot lookups (chart of accounts, tax codes).
7. **Correct concurrency** — already built in C-ERP: advisory locks for document numbers, row locks for payment allocation.
8. **Load test before launch** (k6 / JMeter) at e.g. 1,000 concurrent users; C-ERP's own README lists this as not yet done.

Licensing "unlimited users" (like Tally Gold) is a pricing decision, separate from the above.

## 3. Module parity (C-ERP NavMenu → ribbon) — status 2026-09-25

| Ribbon tab | Sheets | Status (live mode, through the API) |
|---|---|---|
| Home | Dashboard, Quick Entry, Sort/Filter/AutoSum, CSV, Print, Refresh | done |
| Finance | Chart of Accounts, Opening Balances, General Ledger, Journal Voucher (cell entry → post or draft → submit/approve/post), Voucher Register | done |
| Receivables | Customers (+ new), Agents, Estimates (+ convert), Delivery Notes, Sales Invoices (+ print, reminder e-mail, approval workflow), Recurring, Receipts (+ print), Credit Notes, AR Aging, Outstanding, Transactions | done |
| Payables | Vendors (+ new), Purchase Invoices, Expenses, Payment Vouchers, Debit Notes, AP Aging, Expense Transactions | done |
| Items | Items, Item Kits, Units, Item Categories | done (stock lives in the Inventory tab) |
| Inventory *(industry module)* | Stock on Hand, Movements, Valuation vs GL, Reorder Alerts, Warehouses; receipt / issue / transfer / adjustment / issue-for-invoice | done |
| Manufacturing *(industry module)* | BOMs, Production Orders (complete / cancel), Material Requirements | done |
| Jobs · Logistics · Projects *(industry module)* | Jobs with own cost centre + P&L/budget, Shipment Tracker, Vehicles, Trips, Fleet Costs | done |
| CRM & Assets | Leads, Fixed Assets (+ depreciation run) | done |
| HR & Payroll | Employees (+ portal access), Payroll Runs (run / post / pay + WPS SIF), Payslips (print), Leave Requests (approve/reject), Employee Portal | done |
| Reports | Trial Balance, P&L, Balance Sheet, Cash Flow, MIS & Segments, Commission, Audit Log | done |
| Settings | Company Setup, Companies & Access, Manage Team, Industry & Modules (+ GL mapping), Fiscal Periods (+ close), Cost Centers, Currencies, Tax Configuration, Commission Config, Service Kits, Custom Fields | done (read + key actions; full edit forms for every setting remain in C-ERP) |
| AI | Ask AI (Claude, read-only tools), Insights, Scan Bill → draft purchase invoice, AI Audit Trail | done |

## 3b. Industry packs

A company picks its industry under **Settings → Industry & Modules**; that switches module tabs on
and maps the GL accounts module postings use. Modules can also be toggled individually.

| Industry | Default modules | What it adds |
|---|---|---|
| General / services | — | Core accounting, AR/AP, payroll, reports |
| Trading & distribution | Inventory | Warehouses, weighted-average cost, COGS on invoicing, reorder alerts |
| Retail | Inventory | Store stock, sell-through to COGS, counts & adjustments |
| Manufacturing | Inventory + Manufacturing | BOMs, production orders, finished goods at material + conversion cost |
| Logistics & freight | Jobs + Fleet | Shipment job files (mode, AWB/BL, container, ETD/ETA), per-job P&L, vehicles & trips |
| Construction & projects | Jobs + Inventory | Project budgets vs actual cost, site materials |
| Professional / PRO services | Jobs | Engagement profitability (C-ERP's PRO service kits stay available) |

How the modules stay consistent with the books:

- Module data lives in ASCO-owned `asco_*` tables in the **same database** as C-ERP, filtered by the
  same `CurrentCompany`; C-ERP's code and migrations never touch them.
- Every module posting goes through **C-ERP's JournalService** (`GlBridge`) — same numbering, period
  locks, CanPost check and balance rule as any C-ERP document. If saving the module row fails after
  the voucher posted, a reversing voucher is posted automatically (never a voucher without its document).
- Stock valuation is reconciled to the GL inventory account on the Valuation sheet.
- Jobs get their own C-ERP cost centre, so job P&L is read straight from posted GL lines.

## 4. Build plan — all phases delivered

| Phase | Scope | Status |
|---|---|---|
| 0 | Excel shell, all modules as sheets, TS engine + demo books | done |
| 1 | Read API over C-ERP services, sign-in, company switcher, live sheets | done |
| 2 | Write API wrapping C-ERP document services + approval workflow; live entry forms and JV cell entry | done |
| 3 | Print (tax invoice / receipt / payslips), reminder e-mail, payroll run→post→pay + WPS SIF, depreciation, period close, leave approvals, employee self-service portal, settings sheets | done |
| 4 | Ask AI (Claude tool-use, read-only, logged), bill scanning → draft purchase invoice, insights feed | done |
| 5 | Inventory + industry packs (manufacturing, jobs/logistics, fleet) | done |

**Tests:** 10 front-end engine tests + 24 API integration tests (auth, isolation, every read
endpoint per company, TB/BS balance after postings, approval workflow, closed-period and
over-allocation refusals, ESS portal, manufacturing costing, stock↔GL reconciliation, job P&L, AI tools).

### What's still open (honest list)

1. **Browser end-to-end pass on the Phase 3–5 screens** — the API behaviour is covered by the integration
   tests and the UI type-checks and builds, but the new module/AI screens haven't been clicked through in a
   browser yet (the local server was stopped for low memory).
2. **Production migrations for `asco_*` tables** — created on first start today; switch to versioned EF
   migrations before the first client goes live on Postgres.
3. **Load test** at the target concurrency (see §2b) and shared Data Protection keys when running more than
   one API instance.
4. **Settings edit forms** — ASCO has the key settings actions (industry, fiscal periods, currencies,
   tax codes, cost centres, accounts via API); the long-tail settings pages are still edited in C-ERP.
5. **Ask AI needs an Anthropic API key** (`Anthropic:ApiKey`), **Scan Bill needs C-ERP's Gemini key**
   (`Gemini:ApiKey`) — both off until configured; the endpoints say so instead of failing.
6. **Merge** `feature/excel-ui` into `master` and add a remote.
