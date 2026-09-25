# Aegis Books

Excel-style accounting front end covering every module of **C-ERP (Aegis ERP)** — ribbon tabs per
module, name box + formula bar, worksheet grid, sheet tabs, and Excel's status-bar Sum / Count / Average.

**Stack decision and roadmap:** [`docs/STACK-AND-PLAN.md`](docs/STACK-AND-PLAN.md)

## Run

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

Data lives in memory — a refresh resets to the demo books (File → Reset demo data does the same).
Phase 1 of the plan replaces `src/engine/store.ts` with calls to an API over C-ERP's .NET services.

## Layout

```
src/engine/     types, posting engine + read models, seed, store, tests
src/modules/    registry.ts — every screen: tab, group, icon, columns, rows(state)
src/components/ Grid, Ribbon, JournalEntry, DocForm, AiPanel, Backstage
```

Adding a module = adding one entry to `SCREENS` in `src/modules/registry.ts`.
