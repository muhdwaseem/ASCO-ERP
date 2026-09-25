# ASCO

Excel-style accounting front end covering every module of **C-ERP (Aegis ERP)** — ribbon tabs per
module, name box + formula bar, worksheet grid, sheet tabs, and Excel's status-bar Sum / Count / Average.

**Stack decision and roadmap:** [`docs/STACK-AND-PLAN.md`](docs/STACK-AND-PLAN.md)

## Run

Live mode (real books through the .NET API — see [`server/README.md`](server/README.md)):

```bash
dotnet run --project server/Asco.Api --artifacts-path artifacts   # API on :5080
npm run dev                                                      # UI, proxies /api
```

Then sign in with a C-ERP account, or pick **Explore with demo data** for the offline workbook.

Front end only:

```bash
npm install
npm run dev      # http://localhost:5173
npm test         # posting-engine tests (vitest)
npm run build    # type-check + production build
```

## What works in this build

- 55 sheets across 10 ribbon tabs (all C-ERP screens plus planned Stock / ESS / AI items).
- A TypeScript port of C-ERP's posting rules (`src/engine`): every document posts one balanced
  voucher through a single gate (period open, postable accounts, debits = credits); the document
  and its voucher share a number (`INV-2026-0015`).
- Demo books for a UAE advisory firm, seeded through the engine, so the TB, balance sheet and
  AR/AP subledgers reconcile by construction (see `src/engine/ledger.test.ts`).
- Journal Voucher typed straight into cells (Tab skips computed columns, Ctrl+Enter posts).
- New Invoice / Receipt / Bill / Payment / Expense dialogs; Run Depreciation, Run Payroll,
  Mark Paid (WPS), Close Period.
- Sort A→Z / Z→A, Filter, AutoSum, Ctrl+C copies ranges as TSV, CSV export, print, zoom.
- "Tell me what you want to do" (Alt+Q) searches every sheet.

Demo mode keeps data in memory (a refresh resets it). Live mode (Phase 1) is read-only: every sheet with a
`LIVE` entry in `src/modules/live.ts` loads from the API; creating/posting documents through the API is Phase 2.

## Layout

```
src/engine/     types, posting engine + read models, seed, store, tests
src/modules/    registry.ts — every screen: tab, group, icon, columns, rows(state)
src/components/ Grid, Ribbon, JournalEntry, DocForm, AiPanel, Backstage
```

Adding a module = adding one entry to `SCREENS` in `src/modules/registry.ts`.
