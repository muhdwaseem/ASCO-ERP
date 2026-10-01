# ASCO

Excel-style accounting software â€” ribbon tabs, name box + formula bar, worksheet grid, sheet tabs,
status-bar Sum / Count / Average â€” built on **C-ERP's accounting core** (ASP.NET Core 8 + PostgreSQL),
with industry packs for trading, retail, manufacturing, logistics, construction and services, and
Claude-powered AI.

- Stack decision, industry packs, phase status and open items: [`docs/STACK-AND-PLAN.md`](docs/STACK-AND-PLAN.md)
- API design, security and endpoints: [`server/README.md`](server/README.md)
- What has changed and why, code-health checks, tools to re-check: [`docs/KNOWLEDGE_BASE.md`](docs/KNOWLEDGE_BASE.md)
- Hosting for client testing and the security checklist: [`deploy/HOSTING.md`](deploy/HOSTING.md)

## Run

```bash
npm install
dotnet run --project server/Asco.Api --artifacts-path artifacts   # API on :5080 (own dev DB + C-ERP demo data)
npm run dev                                                      # UI on :5173, proxies /api
```

Sign in with a C-ERP account (local demo: `owner@aegiserp.com`), or choose **Explore with demo data**
for an offline workbook that needs no server. Employee logins open the self-service portal.

Optional keys (features stay off, with a clear message, until set):

| Setting | Enables |
|---|---|
| `Anthropic:ApiKey` (or `ANTHROPIC_API_KEY`) | Ask AI â€” Claude with read-only tools over the active company |
| `Gemini:ApiKey` | Scan Bill â€” C-ERP's bill extraction service |
| `Smtp:*` | Invoice reminder e-mails (C-ERP EmailService) |
| `Database:Provider` + `ConnectionStrings:Postgres` | Point the API at the shared C-ERP Postgres database |

## Tests

```bash
npm test                                                        # 10 posting-engine tests (demo engine)
dotnet test server/Asco.Api.Tests --artifacts-path artifacts    # 24 API integration tests
npm run build                                                   # type-check + production build
```

## What's in it

- **Every C-ERP module** as sheets on 10 core ribbon tabs; live data and posting through C-ERP's own
  services (same numbering, VAT, approvals, period locks, per-company roles).
- **Entry the Excel way:** journal vouchers typed into cells (draft â†’ submit â†’ approve â†’ post),
  dialogs for invoices, receipts, bills, payments, expenses, customers, vendors.
- **Documents:** tax invoice, receipt and payslip print layouts; reminder e-mails; WPS SIF export.
- **Industry modules** (per company): Inventory, Manufacturing, Jobs/Logistics/Projects, Fleet.
- **AI:** Ask AI (Claude, read-only, every question logged), Scan Bill â†’ draft purchase invoice,
  Insights feed (overdue, credit limits, drafts, expiring documents, low stock, jobs over budget).
- **Employee portal:** payslips, leave requests + balance, salary advances.
- **Scale-out ready:** stateless API, per-request company scoping, advisory locks for stock and
  numbering on Postgres, login rate-limiting, CSRF header, no-store caching of financial data.

## Layout

```
src/engine/       demo posting engine + seed + tests (offline mode)
src/modules/      registry.ts (every sheet), live.ts (API â†’ sheet mappings)
src/components/   Grid, Ribbon, JournalEntry, DocForm/LiveDocForm, ModuleForm, PrintDoc, AiPanel, ScanBill, EssPortal, SignIn
server/Asco.Api/  API over C-ERP: Read/Write/Ess endpoints, Modules/ (industry packs), Ai/
server/Asco.Api.Tests/  integration tests on a throwaway database
```
