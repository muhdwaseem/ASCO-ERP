using AegisErp.Domain;
using AegisErp.Domain.Entities;

namespace AegisErp.Infrastructure.Services;

/// <summary>A customer with its subledger position (posted documents only).</summary>
public record CustomerSummary(
    int Id, string Code, string Name, string? Trn, int PaymentTermsDays,
    decimal Invoiced, decimal Received, decimal Outstanding, string? Salesperson = null);

/// <summary>The four headline figures on the customer detail view.</summary>
public record CustomerAccountSummary(decimal UnusedCredits, decimal OutstandingReceivables, decimal AdvancePayment, decimal CreditLimit)
{
    public decimal? PercentOfLimitUsed => CreditLimit > 0 ? OutstandingReceivables / CreditLimit : null;
}

/// <summary>One row in a customer statement: an invoice (debit) or receipt (credit).</summary>
public record StatementRow(
    DateOnly Date, string DocNo, string DocType, string Narration,
    decimal Debit, decimal Credit, decimal RunningBalance);

/// <summary>AR aging for one customer, bucketed by days past due as of a reference date.</summary>
public record AgingRow(
    string Code, string Name,
    decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal UnallocatedCredits)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90 - UnallocatedCredits;
}

/// <summary>
/// A posted invoice with money still owing (for receipt allocation pickers). <see cref="BillableLineCount"/>
/// tells the UI whether to offer a per-service breakdown (&gt; 1) or just a single lump amount.
/// </summary>
public record OpenInvoice(int Id, string InvoiceNo, DateOnly Date, DateOnly DueDate, decimal Gross, decimal Outstanding, int BillableLineCount);

/// <summary>A sales invoice for the list page, with its Zoho-style display status and remaining
/// balance computed from payments/credit notes applied — for the invoice list and its status filter.</summary>
public record SalesInvoiceRow(SalesInvoice Invoice, decimal Balance, ArStatus Status);

/// <summary>
/// One invoice line with how much of it has actually been paid, via receipt allocations —
/// so staff can see which specific service on a multi-line invoice is settled vs still owing.
/// Note: only receipt allocations are netted here, not credit notes (see the invoice detail
/// view's disclaimer) — a credit note reduces the invoice-level balance but is not yet
/// attributed to a specific line.
/// </summary>
public record SalesInvoiceLineBalance(int LineId, string Description, string? ItemName, decimal Gross, decimal Allocated)
{
    public decimal Balance => Gross - Allocated;
}

/// <summary>
/// One row on the cross-invoice Transactions view: a single Sales Invoice line, flattened out of
/// its parent invoice, with fulfillment (Completed/Supplier) and payment (Paid From) attribution —
/// so operational staff can track each billed service company-wide without opening every invoice.
/// <see cref="CenterFee"/>/<see cref="GovtCost"/>/<see cref="Expenses"/> mirror
/// <see cref="SalesInvoiceLine"/>'s PRO Service Mode split (Center Fee = the taxable base, Govt
/// Cost/Expenses = the govt fee/bank charge passed through to the customer at cost, no markup).
/// <see cref="Profit"/> is therefore just <see cref="CenterFee"/> — Govt Cost and Expenses are
/// billed at exactly what they cost, so they net to zero and never affect it.
/// <see cref="LinkedDocumentId"/>/<see cref="LinkedDocumentNo"/> are set once this line has been
/// completed via <c>TransactionService.CompleteDirectAsync</c> (a Direct Expense) or
/// <c>CompleteSupplierAsync</c> (a Purchase Invoice) — a real posted document exists for it either
/// way, which is what blocks Reopen on the UI.
/// </summary>
public record TransactionRow(
    int LineId, string TranRef, int InvoiceId, string InvoiceNo, DateOnly InvoiceDate, VoucherStatus InvoiceStatus,
    string CustomerName, string? OrderNo, string Description, string? ItemName, ItemKind? ServiceType,
    decimal Quantity, decimal Amount, bool IsCompleted, DateTime? CompletedAtUtc, string? CompletedBy,
    int? SupplierId, string? SupplierName, string? PaidFromAccountName,
    decimal CenterFee, decimal GovtCost, decimal Vat, decimal Expenses,
    string? ApplicantReference, int? LinkedDocumentId, string? LinkedDocumentNo,
    int? AssignedToEmployeeId, string? AssignedToEmployeeName,
    string? SalespersonName, string? AgentName,
    DateOnly? ExpiryDate, bool HasAttachment)
{
    public decimal Profit => CenterFee;
}
