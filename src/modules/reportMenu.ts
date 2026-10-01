// The Reports ▾ menu (Excel "Get Data" style): categories → reports → optional variants.
// Category and report names follow Zoho Books' Reports Center; each entry opens an ASCO sheet.
// Reports whose screen belongs to a disabled industry module are hidden by the caller.
import type { LucideIcon } from 'lucide-react';
import {
  Boxes, Briefcase, Building2, Calculator, Coins, HandCoins, History, Landmark, Percent, Receipt, Repeat, ShoppingBag, ShoppingCart, TrendingUp, Users, Wallet,
} from 'lucide-react';

export interface ReportItem { label: string; id?: string; sub?: { label: string; id: string }[] }
export interface ReportCategory { label: string; icon: LucideIcon; color: string; items: ReportItem[] }

const G = '#21a366', B = '#4ea1f3', O = '#e8912d', P = '#a97fe0', T = '#34b5a8', R = '#e25d5d';

export const REPORT_MENU: ReportCategory[] = [
  { label: 'Business Overview', icon: TrendingUp, color: G, items: [
    { label: 'Profit and Loss', id: 'pnl' },
    { label: 'Horizontal Profit and Loss (monthly)', id: 'rpt-monthly-pnl' },
    { label: 'Balance Sheet', id: 'balance-sheet' },
    { label: 'Cash Flow Statement', id: 'cash-flow' },
    { label: 'Business Performance Ratios', id: 'rpt-ratios' },
    { label: 'Movement of Equity', id: 'rpt-equity' },
  ] },
  { label: 'Sales', icon: ShoppingCart, color: B, items: [
    { label: 'Sales by Customer', id: 'rpt-sales-by-customer' },
    { label: 'Sales by Item', id: 'rpt-sales-by-item' },
    { label: 'Sales by Sales Person', id: 'rpt-sales-by-salesperson' },
    { label: 'Sales Summary (monthly)', id: 'rpt-sales-summary' },
    { label: 'Commission Report', id: 'commission-report' },
  ] },
  { label: 'Inventory', icon: Boxes, color: O, items: [
    { label: 'Stock Summary', id: 'stock-on-hand' },
    { label: 'Stock Movements', id: 'stock-moves' },
    { label: 'Reorder Alerts', id: 'reorder' },
  ] },
  { label: 'Inventory Valuation', icon: Coins, color: O, items: [
    { label: 'Inventory Valuation Summary', id: 'stock-valuation' },
  ] },
  { label: 'Receivables', icon: HandCoins, color: G, items: [
    { label: 'AR Aging', sub: [{ label: 'AR Aging Summary', id: 'ar-aging' }, { label: 'AR Aging Details', id: 'rpt-ar-aging-details' }] },
    { label: 'Customer Balance Summary', id: 'rpt-customer-balances' },
    { label: 'Invoice Details', id: 'sales-invoices' },
    { label: 'Outstanding Invoices', id: 'outstanding' },
    { label: 'Quote Details', id: 'estimates' },
    { label: 'Delivery Note Details', id: 'delivery-notes' },
  ] },
  { label: 'Payments Received', icon: Wallet, color: G, items: [
    { label: 'Payments Received', id: 'receipts' },
    { label: 'Credit Note Details', id: 'credit-notes' },
    { label: 'Customer Transactions', id: 'transactions' },
  ] },
  { label: 'Recurring Invoices', icon: Repeat, color: T, items: [
    { label: 'Recurring Invoice Details', id: 'recurring-invoices' },
  ] },
  { label: 'Payables', icon: Receipt, color: O, items: [
    { label: 'Vendor Balance Summary', id: 'rpt-vendor-balances' },
    { label: 'AP Aging', sub: [{ label: 'AP Aging Summary', id: 'ap-aging' }, { label: 'AP Aging Details', id: 'rpt-ap-aging-details' }] },
    { label: 'Bill Details', id: 'purchase-invoices' },
    { label: 'Payments Made', id: 'payments' },
    { label: 'Debit Note Details', id: 'debit-notes' },
  ] },
  { label: 'Purchases and Expenses', icon: ShoppingBag, color: R, items: [
    { label: 'Purchases by Vendor', id: 'rpt-purchases-by-vendor' },
    { label: 'Purchases by Item', id: 'rpt-purchases-by-item' },
    { label: 'Expense Details', id: 'expense-transactions' },
    { label: 'Expenses by Category', id: 'rpt-expenses-by-category' },
    { label: 'Expenses by Project / Cost Centre', id: 'cc-pnl' },
  ] },
  { label: 'Taxes', icon: Percent, color: P, items: [
    { label: 'VAT Return Summary (VAT 201)', id: 'rpt-vat-return' },
  ] },
  { label: 'Banking', icon: Landmark, color: B, items: [
    { label: 'Bank & Cash Balances', id: 'rpt-bank-balances' },
    { label: 'Bank Book', id: 'rpt-bank-book' },
  ] },
  { label: 'Projects and Cost Centres', icon: Briefcase, color: T, items: [
    { label: 'Cost Centre / Project P&L', id: 'cc-pnl' },
    { label: 'Job Profitability', id: 'jobs' },
    { label: 'MIS & Segments', id: 'mis' },
  ] },
  { label: 'Fixed Asset', icon: Building2, color: T, items: [
    { label: 'Fixed Asset Register', id: 'assets' },
    { label: 'Depreciation Schedule', id: 'dep-schedule' },
  ] },
  { label: 'Accountant', icon: Calculator, color: B, items: [
    { label: 'Account Transactions (General Ledger)', id: 'general-ledger' },
    { label: 'Account Type Summary', id: 'rpt-account-type-summary' },
    { label: 'Journal Report', id: 'voucher-register' },
    { label: 'Trial Balance', id: 'trial-balance' },
    { label: 'Chart of Accounts with Balances', id: 'chart-of-accounts' },
    { label: 'Opening Balances', id: 'opening-balances' },
    { label: 'Prepayment Schedule', id: 'prepay-schedule' },
  ] },
  { label: 'Payroll', icon: Users, color: P, items: [
    { label: 'Payroll Summary', id: 'payroll' },
    { label: 'Gratuity (EOSB)', id: 'gratuity' },
    { label: 'Expiring Documents', id: 'rpt-expiring-docs' },
  ] },
  { label: 'Activity', icon: History, color: '#9aa0a6', items: [
    { label: 'Audit Log', id: 'audit-log' },
    { label: 'AI Audit Trail', id: 'ai-guardrails' },
  ] },
];
