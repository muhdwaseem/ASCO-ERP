// Demo books, seeded THROUGH the posting engine (never by hand-writing vouchers),
// so the trial balance is in balance by construction — same approach as C-ERP's seed.

import type { Account, AccountType, DocLine, LedgerState } from './types';
import {
  ACC, postCreditNote, postDebitNote, postExpense, postJournal, postPayment, postPayroll,
  postPurchaseInvoice, postReceipt, postSalesInvoice, runDepreciation, invoiceOutstanding, billOutstanding,
} from './ledger';

const U = 'owner@asco.local';

// Standard_Chart_of_Accounts_Import.csv from C-ERP (headers) + postable children.
const HEADERS: [string, string, AccountType, string?, string?, string?][] = [
  ['1000', 'Assets', 'Asset'], ['1100', 'Property Plant & Equipment', 'Asset', '1000', 'Non-Current Assets'],
  ['1150', 'Accumulated Depreciation', 'Asset', '1000', 'Non-Current Assets'], ['1180', 'Other Non-Current Assets', 'Asset', '1000', 'Non-Current Assets'],
  ['1200', 'Inventory', 'Asset', '1000', 'Current Assets'], ['1210', 'Trade Receivables', 'Asset', '1000', 'Current Assets'],
  ['1220', 'Prepayments', 'Asset', '1000', 'Current Assets'], ['1230', 'Other Receivables', 'Asset', '1000', 'Current Assets'],
  ['1240', 'VAT Recoverable', 'Asset', '1000', 'Current Assets'], ['1250', 'Due from Related Parties', 'Asset', '1000', 'Current Assets'],
  ['1260', 'Cash & Bank', 'Asset', '1000', 'Current Assets'],
  ['2000', 'Liabilities', 'Liability'], ['2100', 'Long-term Borrowings', 'Liability', '2000', 'Non-Current Liabilities'],
  ['2110', 'Employee Provisions', 'Liability', '2000', 'Non-Current Liabilities'], ['2200', 'Trade Payables', 'Liability', '2000', 'Current Liabilities'],
  ['2210', 'Accrued Expenses', 'Liability', '2000', 'Current Liabilities'], ['2220', 'VAT Payable', 'Liability', '2000', 'Current Liabilities'],
  ['2230', 'Short-term Provisions', 'Liability', '2000', 'Current Liabilities'], ['2240', 'Due to Related Parties', 'Liability', '2000', 'Current Liabilities'],
  ['2250', 'Other Current Liabilities', 'Liability', '2000', 'Current Liabilities'],
  ['3000', 'Equity', 'Equity'], ['3100', 'Capital', 'Equity', '3000', 'Equity'], ['3200', 'Reserves', 'Equity', '3000', 'Equity'],
  ['3300', 'Retained Earnings', 'Equity', '3000', 'Equity'], ['3400', 'Current Account', 'Equity', '3000', 'Equity'],
  ['4000', 'Income', 'Revenue'], ['4100', 'Sales / Service Revenue', 'Revenue', '4000', 'Revenue', 'Operating Income'],
  ['4150', 'Sales Deductions', 'Revenue', '4000', 'Revenue', 'Operating Income'], ['4200', 'Other Income', 'Revenue', '4000', 'Other Income', 'Non-Operating Income'],
  ['5000', 'Expenses', 'Expense'], ['5100', 'Cost of Goods Sold', 'Expense', '5000', 'Cost of Sales', 'Cost of Goods Sold'],
  ['5200', 'Salaries & Benefits', 'Expense', '5000', 'Employee Costs', 'Operating Expense'], ['5300', 'Administrative Expenses', 'Expense', '5000', 'Admin Expenses', 'Operating Expense'],
  ['5400', 'Selling & Distribution', 'Expense', '5000', 'Selling Expenses', 'Operating Expense'], ['5500', 'Finance Costs', 'Expense', '5000', 'Finance Costs', 'Non-Operating Expense'],
  ['5600', 'Depreciation & Amortisation', 'Expense', '5000', 'Depreciation', 'Operating Expense'], ['5700', 'Other Expenses', 'Expense', '5000', 'Other Expenses', 'Non-Operating Expense'],
];

const CHILDREN: [string, string][] = [
  ['1100-01', 'Furniture & Fixtures'], ['1100-02', 'Motor Vehicles'], ['1100-03', 'IT Equipment'],
  ['1150-01', 'Accumulated Depreciation — PPE'], ['1200-01', 'Stock in Trade'],
  ['1210-01', 'Trade Receivables Control'], ['1220-01', 'Prepaid Rent'], ['1240-01', 'Input VAT 5%'],
  ['1260-01', 'Emirates Bank — Current A/c'], ['1260-02', 'Petty Cash'],
  ['2110-01', 'End of Service Benefits'], ['2200-01', 'Trade Payables Control'], ['2210-01', 'Accrued Salaries'],
  ['2220-01', 'Output VAT 5%'], ['3100-01', 'Share Capital'], ['3300-01', 'Retained Earnings b/f'],
  ['4100-01', 'Professional Service Revenue'], ['4100-02', 'PRO Service Revenue'], ['4150-01', 'Discounts Allowed'], ['4200-01', 'Other Income'],
  ['5100-01', 'Cost of Services — Govt Fees'], ['5200-01', 'Basic Salaries'], ['5200-02', 'Allowances'],
  ['5300-01', 'Office Rent'], ['5300-02', 'Utilities'], ['5300-03', 'Government & Licence Fees'], ['5300-04', 'Software Subscriptions'],
  ['5400-01', 'Marketing & Advertising'], ['5500-01', 'Bank Charges'], ['5600-01', 'Depreciation Expense'], ['5700-01', 'Miscellaneous Expense'],
];

function buildAccounts(): Account[] {
  const heads: Account[] = HEADERS.map(([code, name, type, parent, category, pnlSection]) => ({ code, name, type, isPostable: false, parent, category, pnlSection, currency: 'AED' }));
  const kids: Account[] = CHILDREN.map(([code, name]) => {
    const p = heads.find((h) => h.code === code.slice(0, 4))!;
    return { code, name, type: p.type, isPostable: true, parent: p.code, category: p.category, pnlSection: p.pnlSection, currency: 'AED' };
  });
  return [...heads, ...kids].sort((a, b) => a.code.localeCompare(b.code));
}

const add = (d: string, days: number) => new Date(Date.parse(d) + days * 86_400_000).toISOString().slice(0, 10);
const monthEnd = (y: number, m: number) => new Date(Date.UTC(y, m, 0)).toISOString().slice(0, 10);

export function seed(): LedgerState {
  const s: LedgerState = {
    company: { name: 'Demo Advisory LLC', trn: '100345678900003', baseCurrency: 'AED', address: 'Office 1204, Business Bay, Dubai, UAE', proServiceMode: true },
    accounts: buildAccounts(), vouchers: [],
    customers: [
      { code: 'C-0001', name: 'Al Noor Trading LLC', trn: '100200300400003', email: 'accounts@alnoor.ae', mobile: '+971 50 111 2233', group: 'Trading', currency: 'AED', creditLimit: 50000, paymentTermsDays: 30, salesperson: 'AG-001' },
      { code: 'C-0002', name: 'Gulf Horizon Contracting', trn: '100200300500003', email: 'finance@gulfhorizon.ae', mobile: '+971 55 222 3344', group: 'Construction', currency: 'AED', creditLimit: 80000, paymentTermsDays: 45, salesperson: 'AG-002' },
      { code: 'C-0003', name: 'Emirates Smart Logistics', trn: '100200300600003', email: 'ap@esl.ae', mobile: '+971 52 333 4455', group: 'Logistics', currency: 'AED', creditLimit: 30000, paymentTermsDays: 30, salesperson: 'AG-001' },
      { code: 'C-0004', name: 'Blue Dune Hospitality', trn: '100200300700003', email: 'billing@bluedune.ae', mobile: '+971 56 444 5566', group: 'Hospitality', currency: 'AED', creditLimit: 60000, paymentTermsDays: 30, salesperson: 'AG-002' },
      { code: 'C-0005', name: 'Marina Tech Solutions FZ-LLC', trn: '100200300800003', email: 'accounts@marinatech.ae', mobile: '+971 58 555 6677', group: 'Technology', currency: 'AED', creditLimit: 100000, paymentTermsDays: 15, salesperson: 'AG-001' },
    ],
    vendors: [
      { code: 'V-0001', name: 'City Power & Water', currency: 'AED', creditLimit: 0, paymentTermsDays: 15, group: 'Utilities', email: 'billing@cpw.example' },
      { code: 'V-0002', name: 'Prime Office Supplies LLC', trn: '100900800700003', currency: 'AED', creditLimit: 20000, paymentTermsDays: 30, group: 'Supplies', email: 'sales@primeoffice.example' },
      { code: 'V-0003', name: 'Skyline Properties', trn: '100900800600003', currency: 'AED', creditLimit: 0, paymentTermsDays: 30, group: 'Landlord', email: 'leasing@skyline.example' },
      { code: 'V-0004', name: 'Nexa Cloud Services', currency: 'USD', creditLimit: 0, paymentTermsDays: 30, group: 'Software', email: 'billing@nexa.example' },
      { code: 'V-0005', name: 'Falcon Typing Center', currency: 'AED', creditLimit: 10000, paymentTermsDays: 7, group: 'Govt Services', email: 'desk@falcontyping.example' },
    ],
    salesInvoices: [], purchaseInvoices: [], receipts: [], payments: [], creditNotes: [], debitNotes: [], expenses: [],
    estimates: [], deliveryNotes: [],
    items: [
      { code: 'SRV-001', name: 'Bookkeeping (monthly)', kind: 'Service', unit: 'Month', category: 'Accounting', sellingPrice: 6500, costPrice: 0, salesAccount: ACC.REV, purchaseAccount: ACC.COST_SERV, taxCode: 'SR', isActive: true },
      { code: 'SRV-002', name: 'VAT Return Filing', kind: 'Service', unit: 'Return', category: 'Tax', sellingPrice: 2500, costPrice: 0, salesAccount: ACC.REV, purchaseAccount: ACC.COST_SERV, taxCode: 'SR', isActive: true },
      { code: 'SRV-003', name: 'Statutory Audit Engagement', kind: 'Service', unit: 'Job', category: 'Audit', sellingPrice: 45000, costPrice: 0, salesAccount: ACC.REV, purchaseAccount: ACC.COST_SERV, taxCode: 'SR', isActive: true },
      { code: 'SRV-004', name: 'PRO — Trade Licence Renewal', kind: 'Service', unit: 'Job', category: 'PRO Services', sellingPrice: 4500, costPrice: 1600, salesAccount: ACC.PRO_REV, purchaseAccount: ACC.COST_SERV, taxCode: 'SR', isActive: true },
      { code: 'SRV-005', name: 'Payroll Processing / WPS', kind: 'Service', unit: 'Month', category: 'Payroll', sellingPrice: 3500, costPrice: 0, salesAccount: ACC.REV, purchaseAccount: ACC.COST_SERV, taxCode: 'SR', isActive: true },
      { code: 'GDS-001', name: 'Accounting Software Licence', kind: 'Goods', unit: 'Seat', category: 'Software', sellingPrice: 900, costPrice: 600, salesAccount: ACC.REV, purchaseAccount: ACC.SOFTWARE, taxCode: 'SR', isActive: true },
    ],
    employees: [
      { employeeCode: 'E-001', fullName: 'Sara Al Mansoori', designation: 'Senior Accountant', costCenter: 'CC-ACC', joiningDate: '2021-03-01', status: 'Active', basicSalary: 12000, housingAllowance: 6000, transportAllowance: 1500, otherAllowance: 0, iban: 'AE07 0331 2345 6789 0123 456', visaExpiryDate: '2027-02-28' },
      { employeeCode: 'E-002', fullName: 'Rahul Menon', designation: 'Accountant', costCenter: 'CC-ACC', joiningDate: '2023-06-15', status: 'Active', basicSalary: 8000, housingAllowance: 3500, transportAllowance: 1000, otherAllowance: 500, iban: 'AE07 0331 9876 5432 1098 765', visaExpiryDate: '2026-10-20' },
      { employeeCode: 'E-003', fullName: 'Fatima Khan', designation: 'PRO Officer', costCenter: 'CC-PRO', joiningDate: '2022-01-10', status: 'Active', basicSalary: 6000, housingAllowance: 2500, transportAllowance: 1000, otherAllowance: 0, iban: 'AE07 0260 1111 2222 3333 444', visaExpiryDate: '2026-11-05' },
      { employeeCode: 'E-004', fullName: 'Omar Haddad', designation: 'Audit Manager', costCenter: 'CC-AUD', joiningDate: '2019-09-01', status: 'Active', basicSalary: 18000, housingAllowance: 8000, transportAllowance: 2000, otherAllowance: 1000, iban: 'AE07 0350 4444 5555 6666 777', visaExpiryDate: '2028-01-15' },
      { employeeCode: 'E-005', fullName: 'Grace Dela Cruz', designation: 'Office Administrator', costCenter: 'CC-ADM', joiningDate: '2024-02-01', status: 'Active', basicSalary: 4500, housingAllowance: 2000, transportAllowance: 800, otherAllowance: 0, iban: 'AE07 0331 7777 8888 9999 000', visaExpiryDate: '2027-01-31' },
    ],
    payrollRuns: [],
    assets: [
      { assetCode: 'FA-001', name: 'Office Furniture — Business Bay', category: 'Furniture', purchaseDate: '2026-01-05', purchaseCost: 45000, salvageValue: 0, usefulLifeMonths: 60, status: 'Active' },
      { assetCode: 'FA-002', name: 'Laptops × 6', category: 'IT Equipment', purchaseDate: '2026-01-10', purchaseCost: 27000, salvageValue: 1000, usefulLifeMonths: 36, status: 'Active' },
      { assetCode: 'FA-003', name: 'Company Vehicle — Toyota Corolla', category: 'Vehicles', purchaseDate: '2026-02-01', purchaseCost: 95000, salvageValue: 15000, usefulLifeMonths: 60, status: 'Active' },
    ],
    leads: [
      { name: 'Ahmed Saleh', companyName: 'Desert Rose Cafe', mobile: '+971 50 900 1001', source: 'Website', stage: 'Qualified', estimatedValue: 24000, assignedTo: 'AG-001', lastActivity: '2026-09-20' },
      { name: 'Linda Park', companyName: 'Oasis Dental Clinic', mobile: '+971 55 900 1002', source: 'Referral', stage: 'Proposal', estimatedValue: 42000, assignedTo: 'AG-002', lastActivity: '2026-09-22' },
      { name: 'Yousef Ali', companyName: 'Sharjah Auto Parts', mobile: '+971 52 900 1003', source: 'LinkedIn', stage: 'Contacted', estimatedValue: 15000, assignedTo: 'AG-001', lastActivity: '2026-09-12' },
      { name: 'Meera Iyer', companyName: 'Palm Events FZE', mobile: '+971 56 900 1004', source: 'Walk-in', stage: 'New', estimatedValue: 9000, assignedTo: 'AG-002', lastActivity: '2026-09-24' },
      { name: 'Tom Becker', companyName: 'Harbor Freight Middle East', mobile: '+971 58 900 1005', source: 'Website', stage: 'Won', estimatedValue: 36000, assignedTo: 'AG-001', lastActivity: '2026-08-30' },
    ],
    agents: [
      { agentCode: 'AG-001', name: 'Khalid Rahman', phone: '+971 50 700 1111', email: 'khalid@asco.local', commissionRate: 0.05, status: 'Active' },
      { agentCode: 'AG-002', name: 'Priya Nair', phone: '+971 55 700 2222', email: 'priya@asco.local', commissionRate: 0.04, status: 'Active' },
    ],
    periods: Array.from({ length: 12 }, (_, i) => ({
      name: new Date(Date.UTC(2026, i, 1)).toLocaleString('en', { month: 'short', year: 'numeric', timeZone: 'UTC' }),
      year: 2026, periodNo: i + 1, startDate: `2026-${String(i + 1).padStart(2, '0')}-01`, endDate: monthEnd(2026, i + 1), isClosed: false,
    })),
    taxCodes: [
      { code: 'SR', description: 'Standard Rated 5%', rate: 0.05, taxType: 'Standard', outputAccount: ACC.VAT_OUT, inputAccount: ACC.VAT_IN },
      { code: 'ZR', description: 'Zero Rated 0%', rate: 0, taxType: 'Zero', outputAccount: ACC.VAT_OUT, inputAccount: ACC.VAT_IN },
      { code: 'EX', description: 'Exempt', rate: 0, taxType: 'Exempt', outputAccount: ACC.VAT_OUT, inputAccount: ACC.VAT_IN },
      { code: 'RCM', description: 'Reverse Charge 5% (imports)', rate: 0.05, taxType: 'Reverse Charge', outputAccount: ACC.VAT_OUT, inputAccount: ACC.VAT_IN },
    ],
    costCenters: [
      { code: 'CC-ACC', name: 'Accounting & Bookkeeping', isActive: true }, { code: 'CC-AUD', name: 'Audit & Assurance', isActive: true },
      { code: 'CC-PRO', name: 'PRO Services', isActive: true }, { code: 'CC-ADM', name: 'Administration', isActive: true },
    ],
    currencies: [
      { code: 'AED', name: 'UAE Dirham (base)', rateToBase: 1, isActive: true }, { code: 'USD', name: 'US Dollar', rateToBase: 3.6725, isActive: true },
      { code: 'EUR', name: 'Euro', rateToBase: 4.02, isActive: true }, { code: 'GBP', name: 'Pound Sterling', rateToBase: 4.68, isActive: true },
      { code: 'SAR', name: 'Saudi Riyal', rateToBase: 0.979, isActive: true }, { code: 'INR', name: 'Indian Rupee', rateToBase: 0.044, isActive: false },
    ],
    audit: [],
  };

  const item = (code: string, qty: number, cc?: string): DocLine => {
    const it = s.items.find((i) => i.code === code)!;
    const costCenter = cc ?? (it.category === 'Audit' ? 'CC-AUD' : it.category === 'PRO Services' ? 'CC-PRO' : 'CC-ACC');
    return { description: it.name, account: it.salesAccount, quantity: qty, unitPrice: it.sellingPrice, vatRate: 0.05, costCenter };
  };
  const exp = (description: string, account: string, amount: number, vatRate = 0.05, costCenter = 'CC-ADM'): DocLine =>
    ({ description, account, quantity: 1, unitPrice: amount, vatRate, costCenter });
  const due = (d: string, party: string) => add(d, (s.customers.find((c) => c.code === party) ?? s.vendors.find((c) => c.code === party))!.paymentTermsDays);

  // Opening balances
  postJournal(s, '2026-01-01', 'Opening balances b/f', [
    { account: ACC.BANK, debit: 420000, credit: 0 }, { account: ACC.PETTY, debit: 5000, credit: 0 },
    { account: ACC.CAPITAL, debit: 0, credit: 300000 }, { account: ACC.RETAINED, debit: 0, credit: 125000 },
  ], U, 'OB', 'OB');

  // Fixed asset purchases
  postJournal(s, '2026-01-05', 'Purchase of office furniture (FA-001)', [{ account: ACC.FURNITURE, debit: 45000, credit: 0 }, { account: ACC.BANK, debit: 0, credit: 45000 }], U);
  postJournal(s, '2026-01-10', 'Purchase of laptops (FA-002)', [{ account: ACC.IT, debit: 27000, credit: 0 }, { account: ACC.BANK, debit: 0, credit: 27000 }], U);
  postJournal(s, '2026-02-01', 'Purchase of company vehicle (FA-003)', [{ account: ACC.VEHICLES, debit: 95000, credit: 0 }, { account: ACC.BANK, debit: 0, credit: 95000 }], U);

  // Sales invoices
  const inv = (date: string, party: string, lines: DocLine[]) =>
    postSalesInvoice(s, { party, date, dueDate: due(date, party), lines, agent: s.customers.find((c) => c.code === party)!.salesperson }, U);
  const i1 = inv('2026-01-15', 'C-0001', [item('SRV-001', 3), item('SRV-002', 1)]);
  const i2 = inv('2026-02-10', 'C-0002', [item('SRV-003', 1)]);
  const i3 = inv('2026-03-05', 'C-0003', [item('SRV-004', 2)]);
  const i4 = inv('2026-05-20', 'C-0004', [item('SRV-001', 6)]);
  const i5 = inv('2026-06-15', 'C-0005', [item('SRV-003', 1), item('SRV-002', 1)]);
  const i6 = inv('2026-07-01', 'C-0001', [item('SRV-001', 3)]);
  inv('2026-07-28', 'C-0002', [item('SRV-005', 6)]);
  const i8 = inv('2026-08-18', 'C-0003', [item('SRV-004', 1)]);
  inv('2026-09-02', 'C-0004', [item('SRV-002', 2)]);
  const i10 = inv('2026-09-15', 'C-0005', [item('GDS-001', 5)]);
  const i11 = inv('2026-04-08', 'C-0002', [item('SRV-003', 1)]);
  const i12 = inv('2026-05-05', 'C-0005', [item('SRV-001', 6)]);
  const i13 = inv('2026-06-02', 'C-0003', [item('SRV-003', 1)]);
  inv('2026-08-03', 'C-0004', [item('SRV-001', 6)]);

  postCreditNote(s, { party: 'C-0003', againstDoc: i3, date: '2026-04-01', reason: 'Service fee adjustment', lines: [{ description: 'Goodwill discount', account: ACC.REV, quantity: 1, unitPrice: 1000, vatRate: 0.05 }] }, U);

  const rv = (date: string, party: string, against: string, amount?: number) =>
    postReceipt(s, { party, againstDoc: against, date, bankAccount: ACC.BANK, amount: amount ?? invoiceOutstanding(s, against), paymentMode: 'Bank Transfer', reference: `TT-${against.slice(-4)}` }, U);
  rv('2026-02-10', 'C-0001', i1); rv('2026-03-20', 'C-0002', i2); rv('2026-04-15', 'C-0003', i3, 3000);
  rv('2026-06-25', 'C-0004', i4); rv('2026-07-10', 'C-0005', i5, 10000); rv('2026-08-05', 'C-0001', i6);
  rv('2026-05-12', 'C-0002', i11); rv('2026-06-10', 'C-0005', i12); rv('2026-07-20', 'C-0003', i13, 30000);

  // Purchases & payables
  const bill = (date: string, party: string, lines: DocLine[]) => postPurchaseInvoice(s, { party, date, dueDate: due(date, party), lines }, U);
  const p1 = bill('2026-01-01', 'V-0003', [exp('Office rent — annual lease 2026', ACC.RENT, 60000)]);
  const p2 = bill('2026-03-01', 'V-0004', [exp('Cloud & software — Q1/Q2', ACC.SOFTWARE, 4800)]);
  const p3 = bill('2026-06-10', 'V-0002', [exp('Stationery & office supplies', ACC.MISC, 3200)]);
  bill('2026-08-20', 'V-0005', [exp('Govt fees — client trade licences', ACC.COST_SERV, 5200, 0, 'CC-PRO')]);
  bill('2026-09-01', 'V-0004', [exp('Cloud & software — Q3/Q4', ACC.SOFTWARE, 4800)]);

  postDebitNote(s, { party: 'V-0002', againstDoc: p3, date: '2026-06-20', reason: 'Damaged supplies returned', lines: [exp('Returned damaged toner', ACC.MISC, 400)] }, U);

  const pv = (date: string, party: string, against: string) =>
    postPayment(s, { party, againstDoc: against, date, bankAccount: ACC.BANK, amount: billOutstanding(s, against), paymentMode: 'Bank Transfer' }, U);
  pv('2026-01-05', 'V-0003', p1); pv('2026-03-25', 'V-0004', p2); pv('2026-07-01', 'V-0002', p3);

  // Direct expenses
  postExpense(s, { vendor: 'V-0001', date: '2026-02-28', bankAccount: ACC.BANK, narration: 'Electricity & water — Feb', lines: [exp('Utilities Feb', ACC.UTIL, 1850)] }, U);
  postExpense(s, { date: '2026-04-12', bankAccount: ACC.BANK, narration: 'Google & LinkedIn ads', lines: [exp('Digital marketing', ACC.MARKETING, 6000)] }, U);
  postExpense(s, { date: '2026-05-31', bankAccount: ACC.BANK, narration: 'Bank charges — May', lines: [exp('Bank charges', ACC.BANK_CHG, 250, 0)] }, U);
  postExpense(s, { vendor: 'V-0001', date: '2026-08-31', bankAccount: ACC.BANK, narration: 'Electricity & water — Aug', lines: [exp('Utilities Aug', ACC.UTIL, 2100)] }, U);
  postExpense(s, { date: '2026-09-10', bankAccount: ACC.PETTY, narration: 'Immigration fees — staff visa renewal', lines: [exp('Visa renewal fees', ACC.GOVT, 3400, 0)] }, U);

  // Payroll (Jul, Aug paid via WPS; Sep accrued)
  postPayroll(s, 'Jul 2026', '2026-07-31', U, true);
  postPayroll(s, 'Aug 2026', '2026-08-31', U, true);

  // Depreciation Jan–Aug
  for (let m = 1; m <= 8; m++) runDepreciation(s, monthEnd(2026, m), U);

  // Estimates & delivery notes (non-posting documents)
  s.estimates.push(
    { no: 'EST-2026-0001', party: 'C-0005', date: '2026-06-01', validUntil: '2026-06-30', status: 'Converted', lines: [item('SRV-003', 1), item('SRV-002', 1)] },
    { no: 'EST-2026-0002', party: 'C-0004', date: '2026-09-10', validUntil: '2026-10-10', status: 'Sent', lines: [item('SRV-003', 1)] },
    { no: 'EST-2026-0003', party: 'C-0002', date: '2026-09-21', validUntil: '2026-10-21', status: 'Draft', lines: [item('SRV-005', 12)] },
  );
  s.deliveryNotes.push(
    { no: 'DLN-2026-0001', party: 'C-0005', againstDoc: i10, date: '2026-09-15', address: 'Marina Plaza, Dubai Marina', status: 'Delivered', qty: 5, description: 'Accounting Software Licence' },
    { no: 'DLN-2026-0002', party: 'C-0003', againstDoc: i8, date: '2026-08-19', address: 'JAFZA South, Dubai', status: 'Delivered', qty: 1, description: 'Renewed trade licence documents' },
  );

  // Close H1
  for (const p of s.periods) if (p.periodNo <= 6) p.isClosed = true;
  s.audit.unshift({ at: '2026-07-05 10:12', user: U, action: 'Closed period', entity: 'Fiscal Period', detail: 'Jan–Jun 2026 closed for posting' });
  s.audit.push({ at: '2026-03-02 09:40', user: 'accounts@asco.local', action: 'Reassigned', entity: 'Customer', detail: 'C-0003 salesperson AG-002 → AG-001' });
  return s;
}
