namespace AegisErp.Domain;

/// <summary>
/// Control accounts the posting engine must be able to resolve by code.
/// Subledger documents (invoices, receipts) post against these.
/// </summary>
public static class WellKnownAccounts
{
    public const string AccountsReceivable = "12010";
    public const string VatPayable = "22010";

    /// <summary>AP control account — purchase invoices credit it; payments and debit notes debit it.</summary>
    public const string AccountsPayable = "21010";

    /// <summary>Recoverable input VAT on purchases (sits in the prepaid/VAT-input asset account).</summary>
    public const string VatInput = "13010";

    /// <summary>Unearned revenue (Liability) — where a sales invoice line's credit lands when
    /// its <see cref="Domain.RevenueRecognition"/> is Deferred, instead of a P&amp;L revenue account.</summary>
    public const string DeferredRevenue = "23010";

    /// <summary>Contra-asset (credit-normal, nets against fixed assets on the Balance Sheet) —
    /// fixed-asset depreciation runs credit this control account instead of crediting each asset
    /// directly.</summary>
    public const string AccumulatedDepreciation = "15010";

    /// <summary>Liability — payroll runs credit this for each employee's net pay; paying salaries
    /// out later debits it against Bank.</summary>
    public const string SalariesPayable = "21020";

    /// <summary>Asset — debited when a <see cref="Entities.SalaryAdvance"/> is issued (Cr Bank),
    /// credited as each payroll run repaying it posts (Cr this / Dr reduces that run's net pay via
    /// <see cref="Entities.PayrollRunLine.SalaryAdvanceDeduction"/>). See
    /// <c>SalaryAdvanceService</c>/<c>PayrollService.PostRunAsync</c>.</summary>
    public const string EmployeeAdvancesReceivable = "12040"; // placeholder — confirm a free code in a live client CoA

    /// <summary>Liability — gratuity (End of Service Benefits) postings credit this; paying it out
    /// later debits it against Bank. See <c>GratuityService</c>.</summary>
    public const string GratuityPayable = "21040";

    /// <summary>Liability — a "Pay Later" Direct Expense credits this instead of Bank at posting;
    /// the later payoff debits it against Bank. See <c>DirectExpensePaymentService</c>.
    /// v1 limitation: input VAT on a Pay-Later expense is recovered in full at posting time and is
    /// never automatically reversed under the UAE VAT Executive Regulations' Article 55 six-month
    /// non-payment rule (input tax must be repaid if unpaid &gt;6 months past due, then re-claimed
    /// once paid). Flag long-outstanding balances on this account for manual review.</summary>
    public const string ExpensesPayable = "21050";

    /// <summary>P&amp;L Expense — the actual government fee paid out at Transactions "Complete →
    /// Direct" time (see <c>TransactionService.CompleteDirectAsync</c>), Dr'd against the chosen
    /// "Govt Payment Account". Deliberately NOT netted against the govt fee already billed to the
    /// customer — that amount was already credited gross to the line's own Revenue account at
    /// invoice-posting time (<c>SalesInvoiceService.BuildVoucherLines</c>), since PRO-service
    /// invoices carry no separate govt-fee clearing/liability account today. Revenue booked gross
    /// at invoicing + Expense booked gross at actual payment nets to the correct Net Profit even
    /// though both totals run higher than the "clean" Center-Fee-only figures. Not seeded
    /// automatically; must be added to each PRO-Service company's live Chart of Accounts (Expense
    /// type) before this feature can be used there.
    /// v1 limitation (Bookkeeper &amp; Controller-reviewed): the amount posted here is capped at —
    /// but not required to equal — what was billed for that line's Govt Fee, and a line can only
    /// be completed once. If the actual amount paid is less than billed, the difference quietly
    /// improves margin on what's supposed to be an at-cost pass-through, with nothing in the GL
    /// distinguishing that from genuine Center Fee profit. Not a compliance issue (it still flows
    /// through P&amp;L correctly), but flag for a future "billed vs. actual Govt Fee" report if this
    /// variance needs visibility.</summary>
    public const string GovtFeesExpense = "51500"; // placeholder — confirm a free code in a live client CoA

    /// <summary>P&amp;L Expense — a supplier's OWN handling/service charge for an outsourced PRO
    /// service job, at Transactions "Complete → Supplier" time (see
    /// <c>TransactionService.CompleteSupplierAsync</c>). Deliberately kept separate from
    /// <see cref="GovtFeesExpense"/> (Bookkeeper &amp; Controller-reviewed): that account's whole
    /// purpose is a 1:1 mirror of the Govt Fee already billed to the customer — a supplier's own
    /// markup has no revenue-side twin, isn't capped against anything, and is a genuine
    /// subcontracting cost the business chose to incur, not an at-cost pass-through. Blending the
    /// two would break flux analysis (a rise in "Government Fees Expense" could mean either govt
    /// fees rose or subcontractor markups did) and audit substantiation (this account should only
    /// ever trace to vendor invoices with an embedded service charge, never government receipts).
    /// Not seeded automatically; must be added to each PRO-Service company's live Chart of Accounts
    /// (Expense type) before this feature can be used there.</summary>
    public const string SubcontractedProServicesExpense = "51510"; // placeholder — confirm a free code in a live client CoA

    /// <summary>Liability — net VAT settlement (Output − Input) for the periodic return. Not
    /// posted to by any transaction directly; a manual/period-end entry nets <see cref="VatPayable"/>
    /// and <see cref="VatInput"/> into this account ahead of paying or reclaiming the balance from
    /// the FTA. Not seeded automatically — created on demand by the VAT Master's Auto-configure.</summary>
    public const string VatControl = "22020";

    /// <summary>Liability — the payable leg of reverse-charge (RCM) self-accounting on an import.
    /// Not posted to by any transaction directly yet — mappable now via the VAT Master so a Reverse
    /// Charge tax code has somewhere to point, posted to once RCM posting logic ships as its own
    /// feature. Not seeded automatically.</summary>
    public const string ReverseChargeVatPayable = "22030";

    /// <summary>Asset — the recoverable leg of reverse-charge (RCM) self-accounting on an import.
    /// Same status as <see cref="ReverseChargeVatPayable"/> — mappable now, posted to later. Not
    /// seeded automatically.</summary>
    public const string ReverseChargeVatRecoverable = "13020";
}
