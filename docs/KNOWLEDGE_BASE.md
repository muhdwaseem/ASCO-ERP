# ASCO knowledge base

What ASCO is, where everything lives, and **every change made so far** in plain words.
Update this file whenever something is added. Git history is the detailed record (`git log`).

Last updated: 01 Oct 2026 · Branch `feature/excel-ui` · Worktree `D:\Claude-Projects\aegis-books\.claude\worktrees\excel-ui`

---

## 1. How ASCO is built

```
Browser (React, Excel-style UI) --/api--> ASCO API (.NET 8) --> C-ERP accounting core (unchanged) --> database
                                               \--> ASCO's own tables (asco_*) for modules C-ERP doesn't have
```

| Part | Where | Notes |
|---|---|---|
| Excel-style UI | `src/` | React 19 + TypeScript + Vite |
| ASCO API | `server/Asco.Api/` | ASP.NET Core minimal API |
| Accounting core | `D:\C-ERP\C_ERP-main\src` | Referenced, **never edited** from this project. C-ERP changes are made in C-ERP's own chat. |
| ASCO-only data | `asco_*` tables (`Modules/ModulesDbContext.cs`) | Inventory, manufacturing, jobs, fleet, AI log, prepayments |
| Tests | `server/Asco.Api.Tests/` (API, 34), `src/engine/ledger.test.ts` (10) | |
| Hosting | `deploy/` | `publish.ps1`, `run-hosted.cmd`, `HOSTING.md` |

**Rules every change follows:**
- Business rules and postings go through C-ERP's own services. ASCO's own postings go through `GlBridge`, which reverses the voucher if ASCO's save fails.
- Every request is checked against the company in the `X-Company-Id` header, plus post / payroll / admin rights.
- Every change is type-checked, unit- and API-tested, clicked through in a real browser, then committed.

## 2. Where to find things

| You want… | Look in |
|---|---|
| A screen / ribbon button | `src/modules/registry.ts` (screens), `src/App.tsx` (ribbon actions) |
| Which API feeds a sheet | `src/modules/live.ts`, `src/modules/reportsLive.ts` |
| The Reports ▾ menu | `src/modules/reportMenu.ts` |
| Report date filters | `src/modules/reportDates.ts`, `src/components/ReportDates.tsx` |
| Typing documents into cells | `src/components/EntrySheets.tsx` |
| Gratuity / depreciation / prepayment / cost-centre screens | `src/components/AccountingSheets.tsx` |
| Shared sheet helpers | `src/modules/sheetKit.ts` |
| Read / write endpoints | `server/Asco.Api/ReadEndpoints.cs`, `WriteEndpoints.cs` |
| Gratuity, assets, prepayments, cost-centre P&L API | `server/Asco.Api/Modules/AccountingEndpoints.cs` |
| Extra reports API | `server/Asco.Api/Modules/ReportEndpoints.cs` |
| Security settings | `server/Asco.Api/Security.cs`, `Program.cs` |

## 3. Change log (oldest first)

| Date | Commit | What changed | Why |
|---|---|---|---|
| 25 Sep | `9b861cc`, `6c7ccea` | Excel-style app (ribbon, formula bar, grid, sheet tabs) with every C-ERP module as sheets on demo data | Match the requested Excel look |
| 25 Sep | `2894f0d`, `c7c09b5` | Named ASCO; stack comparison (Tally/Zoho/Odoo/Dynamics) and unlimited-users plan in `docs/STACK-AND-PLAN.md` | Choose the technology: .NET core chosen |
| 25 Sep | `9fab5c3` | Phase 1: read API over C-ERP + live mode (sign in, company switch) | Real data instead of demo |
| 25 Sep | `5afb713`, `4ce5fef` | Phases 2-3: posting (invoices, receipts, bills, payments, expenses, vouchers, approvals), print, payroll + WPS, employee self-service, settings | Full accounting cycle |
| 25 Sep | `180f503`, `e9e7bd0` | Industry modules: inventory, manufacturing, jobs/logistics, fleet; per-company module tabs | Works for trading, manufacturing, logistics, construction |
| 25 Sep | `2260e14`, `fc3f114` | Phase 4: Ask AI (Claude, read-only tools), bill scanning, insights, AI audit log | AI features |
| 26-28 Sep | `33da8e4`, `b9dbebb` | `start-asco.cmd` builds, then starts API and UI | One-click local start |
| 01 Oct | `833c30a` | New Credit Note / Debit Note | Requested |
| 01 Oct | `a015a18` | New Quotation, status, print, convert to invoice | Requested |
| 01 Oct | `bf49818`, `4e3a21c` | Fast entry: type invoices, quotations, bills, receipts, payments, expenses straight into the cells; Ctrl+Enter posts | Faster than pop-up forms |
| 01 Oct | `87931ad` | Cost centre / project on every line + Cost Centre P&L; UAE gratuity (provision, leaver settlement); asset register, depreciation schedule, disposal; prepayments with monthly release | Requested modules |
| 01 Oct | `a847b63` | Reports ▾ menu in Excel "Get Data" style: 16 Zoho categories, 56 entries, 19 new reports (ratios, VAT 201, bank book, sales by item…) | Organise reports |
| 01 Oct | `30b0d2f`, `6a2b3c4` | Date filter on every dated report; security hardening; hosting package | Requested; preparing for client testing |
| 01 Oct | `6112451` | Removed copied helper code (now `sheetKit.ts`) | Found by the code-health check below |
| 01 Oct | `5069380` | **New Employees** sheet (HR & Payroll), typed into cells like the other batch sheets | Gap found while writing the client test guide: the API could add employees but no screen did, so a fresh hosted copy could not test payroll or gratuity |

Not committed to git: the test data posted in the local demo company Aegis FZE while testing (expenses, a laptop asset, a rent prepayment, two employees). It lives only in `server/Asco.Api/asco_dev.db`.

## 4. Code health check (01 Oct 2026)

| Check | Tool | Result |
|---|---|---|
| Repeated code | `npx jscpd src server/Asco.Api --min-lines 6` | 7 small clones (0.48%), plus small helpers copied into 5 files. **Helpers fixed** (`6112451`). Remaining: 3 clones between the demo forms and live forms (`DocForm` vs `LiveDocForm`, `ModuleForm`), 1 in print layout, and 1 short preamble in `AccountingEndpoints.cs`. Low risk, left as is. |
| Compiler warnings (ASCO code) | `dotnet build` | 0 |
| Lint | `npx oxlint src` | No errors. Warnings are dev-only "fast refresh" notices and a few React hook dependency hints in `App.tsx`. The extra dependencies are intentional: they force a refresh when the entry/tool sheets change. |
| Vulnerable packages | `dotnet list package --vulnerable`, `npm audit` | 0 |
| Tests | `dotnet test`, `npx vitest run` | 34/34 API, 10/10 unit |
| Missing screens | Walking the client test script against a clean database | **Found:** no way to add employees in ASCO → fixed (New Employees). Rebuild the hosting package (`deploy\publish.ps1`) before the client starts. |

**Known issues / open items**
- The demo database contains test postings (see above). Hosted copies use their own clean database.
- `/customers` and `/vendors` balance summaries are "as of today" only (no date filter).
- Zoho reports with no data in ASCO are not included: timesheets, sales/purchase orders, budgets, currency gain/loss.
- Before real client data: personal logins + two-factor sign-in, Postgres with backups, restricted `AllowedHosts` (see `deploy/HOSTING.md`).

## 5. How to re-check it yourself

Commands (from the worktree folder):
```powershell
npx tsc -b; npx vitest run                                                    # type check + unit tests
dotnet test server/Asco.Api.Tests/Asco.Api.Tests.csproj --artifacts-path artifacts   # API tests (stop the running API first)
npx jscpd src server/Asco.Api --ignore "**/obj/**,**/bin/**" --min-lines 6    # repeated code
npx oxlint src                                                                # lint
dotnet list server/Asco.Api/Asco.Api.csproj package --vulnerable; npm audit   # vulnerable packages
git log --oneline                                                             # every change
```

Skills and agents already installed that do this job (type the slash command in Claude Code):

| Need | Use |
|---|---|
| Review a change for bugs before merging | `/code-review` (or `/review`) |
| Find repeated / dead code, risky hotspots | `ripwire-fresh-eyes` skill (clones, dead code, bus factor) |
| Clean up repeated or over-complicated code | `/simplify` |
| Security review | `/security-review`, `/cso`; agents **Application Security Engineer**, **AI-Generated Code Security Auditor** |
| Drift across many AI sessions (code vs docs mismatch, silent changes) | agent **Codebase Archaeologist** |
| Overall code-quality dashboard | `/health` |
| Click through the app and fix bugs | `/qa` (or `/qa-only` for a report) |
| Write or update docs | `/document-release`, `/document-generate` |
| Code-quality bar for new code | `ripwire-quality-bar` skill; agent **Code Reviewer** |
