namespace AegisErp.Infrastructure.Services;

/// <summary>A vendor with its subledger position (posted documents only).</summary>
public record VendorSummary(
    int Id, string Code, string Name, string? Trn, int PaymentTermsDays,
    decimal Billed, decimal Paid, decimal Outstanding);

/// <summary>AP aging for one vendor, bucketed by days past due as of a reference date.</summary>
public record ApAgingRow(
    string Code, string Name,
    decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal UnallocatedDebits)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90 - UnallocatedDebits;
}

/// <summary>A posted purchase invoice with money still owing (for payment allocation pickers).</summary>
public record OpenPurchaseInvoice(int Id, string InvoiceNo, DateOnly Date, DateOnly DueDate, decimal Gross, decimal Outstanding, int BillableLineCount);

/// <summary>One purchase invoice line with how much of it has actually been paid, via vendor
/// payment allocations — so staff can see which specific charge on a multi-line bill is settled.</summary>
public record PurchaseInvoiceLineBalance(int LineId, string Description, decimal Gross, decimal Allocated)
{
    public decimal Balance => Gross - Allocated;
}

/// <summary>One "Pay Later" Direct Expense line with how much of it has actually been paid, via
/// <see cref="Domain.Entities.DirectExpensePaymentAllocation"/> — mirrors <see cref="PurchaseInvoiceLineBalance"/>.</summary>
public record DirectExpenseLineBalance(int LineId, string? Description, decimal Gross, decimal Allocated)
{
    public decimal Balance => Gross - Allocated;
}
