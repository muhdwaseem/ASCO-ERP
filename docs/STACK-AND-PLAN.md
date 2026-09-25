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

## 3. Module parity (C-ERP NavMenu → ribbon)

| Ribbon tab | Sheets | Prototype status |
|---|---|---|
| Home | Dashboard, Quick Entry (invoice/receipt/bill/payment/expense), Sort/Filter/AutoSum, CSV, Print | working |
| Finance | Chart of Accounts, Opening Balances, General Ledger, Journal Voucher (cell entry + Post), Voucher Register | working |
| Receivables | Customers, Agents, Estimate/Quotation, Delivery Note, Sales Invoice, Recurring Invoices, Receipt Voucher, Credit Note, AR Aging, Outstanding Report, Transactions | working (new invoice/receipt via dialog) |
| Payables | Vendors, Purchase Invoice, Expenses, Payment Voucher, Debit Note, AP Aging, Expense Transactions | working (new bill/payment/expense via dialog) |
| Items | Items, Item Kits, Units, Item Categories, Stock & Warehouses | working; stock = planned (also missing in C-ERP) |
| CRM & Assets | Leads, Fixed Assets (+ Run Depreciation) | working |
| HR & Payroll | Employees (UAE EOSB gratuity, visa expiry), Payroll Runs (+ Run / Mark Paid WPS), WPS/Payslips, Employee Portal | working; ESS portal = Phase 3 |
| Reports | Trial Balance, P&L, Balance Sheet, Cash Flow, MIS & Segments, Commission Report, Audit Log | working |
| Settings | Company Setup, Companies & Access, Manage Team, Fiscal Periods (+ Close Period), Cost Centers, Currencies, Tax Configuration, Commission Config, Service Kits, Custom Fields & Tags | read views working; edit dialogs = Phase 2 |
| AI | Ask AI (offline deterministic answers), Insights (rule-based anomalies), Scan Bill, AI Audit Trail | UX in place; Claude wiring = Phase 4 |

## 4. Build plan

| Phase | Scope | Rough effort |
|---|---|---|
| **0 — done** | Excel shell, all modules as sheets, TS posting engine + demo books, 10 engine tests | — |
| **1 — API (read)** | `AegisErp.Api`: auth, company switcher, GET endpoints for every list/report sheet; OpenAPI → TS client; swap `store` reads for TanStack Query | 1–2 weeks |
| **2 — API (write)** | POST endpoints wrapping existing document services (invoice, receipt, bill, payment, CN/DN, expense, JV incl. Draft→Approve workflow, masters CRUD); replace `store.mutate` with API calls; server errors → status bar | 2–3 weeks |
| **3 — Depth** | Invoice/estimate/payslip print layouts & PDF, email, imports, recurring generation, multi-currency, ESS portal, custom fields | 2–3 weeks |
| **4 — AI** | Ask AI via Claude tool-use, bill scanning into a draft Purchase Invoice, anomaly feed, eval set (port `tools/AiQueryEval`) | 2 weeks |
| **5 — Inventory** | Warehouses, stock moves, valuation (FIFO/avg), COGS posting | 2–3 weeks |

Rule to keep throughout: **the UI never computes posted numbers the server disagrees with** — once
Phase 1 lands, the .NET engine is the single source of truth and `src/engine` becomes a
test fixture / offline demo only.
