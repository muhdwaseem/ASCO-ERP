// Domain model — a TypeScript mirror of the C-ERP (Aegis ERP) entities.
// Field names follow src/AegisErp.Domain/Entities so the API contract maps 1:1 later.

export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense';

export interface Account {
  code: string;
  name: string;
  type: AccountType;
  isPostable: boolean;
  parent?: string;
  category?: string;
  pnlSection?: string;
  currency: string;
}

export interface JournalLine {
  account: string;
  debit: number;
  credit: number;
  narration?: string;
  party?: string; // customer / vendor / employee code — the subledger key
  costCenter?: string;
}

export type VoucherType =
  | 'OB' | 'JV' | 'INV' | 'RV' | 'CN' | 'PINV' | 'PV' | 'DN' | 'EXP' | 'PAY' | 'DEP';

export interface JournalVoucher {
  voucherNo: string;
  type: VoucherType;
  date: string; // yyyy-mm-dd
  narration: string;
  reference?: string;
  status: 'Posted' | 'Void';
  createdBy: string;
  lines: JournalLine[];
}

export interface Party {
  code: string;
  name: string;
  trn?: string;
  email?: string;
  mobile?: string;
  group?: string;
  currency: string;
  creditLimit: number;
  paymentTermsDays: number;
  salesperson?: string;
}

export interface DocLine {
  description: string;
  account: string; // revenue (sales) or expense (purchase) account
  quantity: number;
  unitPrice: number;
  vatRate: number; // 0.05 = 5% UAE standard rate
  costCenter?: string;
}

export interface TradeDoc {
  no: string;
  party: string;
  date: string;
  dueDate: string;
  status: 'Posted' | 'Void';
  narration?: string;
  agent?: string;
  lines: DocLine[];
}

export interface Settlement {
  no: string;
  party: string;
  againstDoc?: string; // allocation target; undefined = on account
  date: string;
  bankAccount: string;
  amount: number;
  paymentMode: 'Bank Transfer' | 'Cheque' | 'Cash' | 'Card';
  reference?: string;
}

export interface NoteDoc {
  no: string;
  party: string;
  againstDoc?: string;
  date: string;
  reason: string;
  lines: DocLine[];
}

export interface DirectExpense {
  no: string;
  vendor?: string;
  date: string;
  bankAccount: string;
  narration: string;
  lines: DocLine[];
}

export interface Item {
  code: string;
  name: string;
  kind: 'Service' | 'Goods';
  unit: string;
  category: string;
  sellingPrice: number;
  costPrice: number;
  salesAccount: string;
  purchaseAccount: string;
  taxCode: string;
  isActive: boolean;
}

export interface Employee {
  employeeCode: string;
  fullName: string;
  designation: string;
  costCenter: string;
  joiningDate: string;
  status: 'Active' | 'Terminated';
  basicSalary: number;
  housingAllowance: number;
  transportAllowance: number;
  otherAllowance: number;
  iban: string;
  visaExpiryDate: string;
}

export interface PayrollRun {
  no: string;
  period: string;
  runDate: string;
  isPaid: boolean;
  employees: string[];
}

export interface FixedAsset {
  assetCode: string;
  name: string;
  category: string;
  purchaseDate: string;
  purchaseCost: number;
  salvageValue: number;
  usefulLifeMonths: number;
  status: 'Active' | 'Disposed';
}

export interface Lead {
  name: string;
  companyName: string;
  mobile: string;
  source: string;
  stage: 'New' | 'Contacted' | 'Qualified' | 'Proposal' | 'Won' | 'Lost';
  estimatedValue: number;
  assignedTo: string;
  lastActivity: string;
}

export interface Estimate {
  no: string;
  party: string;
  date: string;
  validUntil: string;
  status: 'Draft' | 'Sent' | 'Accepted' | 'Converted' | 'Declined';
  lines: DocLine[];
}

export interface DeliveryNote {
  no: string;
  party: string;
  againstDoc?: string;
  date: string;
  address: string;
  status: 'Draft' | 'Delivered';
  qty: number;
  description: string;
}

export interface Agent {
  agentCode: string;
  name: string;
  phone: string;
  email: string;
  commissionRate: number;
  status: 'Active' | 'Inactive';
}

export interface FiscalPeriod {
  name: string;
  year: number;
  periodNo: number;
  startDate: string;
  endDate: string;
  isClosed: boolean;
}

export interface TaxCode {
  code: string;
  description: string;
  rate: number;
  taxType: 'Standard' | 'Zero' | 'Exempt' | 'Reverse Charge';
  outputAccount: string;
  inputAccount: string;
}

export interface LedgerState {
  company: { name: string; trn: string; baseCurrency: string; address: string; proServiceMode: boolean };
  accounts: Account[];
  vouchers: JournalVoucher[];
  customers: Party[];
  vendors: Party[];
  salesInvoices: TradeDoc[];
  purchaseInvoices: TradeDoc[];
  receipts: Settlement[];
  payments: Settlement[];
  creditNotes: NoteDoc[];
  debitNotes: NoteDoc[];
  expenses: DirectExpense[];
  estimates: Estimate[];
  deliveryNotes: DeliveryNote[];
  items: Item[];
  employees: Employee[];
  payrollRuns: PayrollRun[];
  assets: FixedAsset[];
  leads: Lead[];
  agents: Agent[];
  periods: FiscalPeriod[];
  taxCodes: TaxCode[];
  costCenters: { code: string; name: string; isActive: boolean }[];
  currencies: { code: string; name: string; rateToBase: number; isActive: boolean }[];
  audit: { at: string; user: string; action: string; entity: string; detail: string }[];
}
