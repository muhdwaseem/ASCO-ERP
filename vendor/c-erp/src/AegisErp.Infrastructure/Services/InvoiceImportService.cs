using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One parsed CSV row for a historical/opening invoice import — same shape for both the
/// Sales (Customer) and Purchase (Vendor) side. <see cref="Amount"/> must be the balance still
/// outstanding today, not the document's original total.</summary>
public record ImportInvoiceRow(
    string PartyName, string? Reference, DateOnly Date, DateOnly? DueDate, decimal Amount, string? Notes);

/// <summary>Outcome of importing one row — mirrors <see cref="ImportRowResult"/> (Chart of
/// Accounts import) so both dialogs render the same success/failure table shape.</summary>
public record ImportInvoiceRowResult(int RowNumber, string PartyName, bool Success, string Message);

/// <summary>
/// Orchestrates a bulk import of historical Sales/Purchase invoices from a prior system: matches
/// (or creates) the named Customer/Vendor, then hands each row to
/// <see cref="SalesInvoiceService.ImportOpeningInvoiceAsync"/> /
/// <see cref="PurchaseInvoiceService.ImportOpeningInvoiceAsync"/>, which does the actual posting.
/// One bad row is reported without blocking the rest, matching
/// <see cref="ChartOfAccountsService.ImportAsync"/>'s pattern.
/// </summary>
public class InvoiceImportService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly CustomerService _customers;
    private readonly VendorService _vendors;
    private readonly SalesInvoiceService _salesInvoices;
    private readonly PurchaseInvoiceService _purchaseInvoices;

    public InvoiceImportService(
        IDbContextFactory<AegisDbContext> dbf, CustomerService customers, VendorService vendors,
        SalesInvoiceService salesInvoices, PurchaseInvoiceService purchaseInvoices)
    {
        _dbf = dbf;
        _customers = customers;
        _vendors = vendors;
        _salesInvoices = salesInvoices;
        _purchaseInvoices = purchaseInvoices;
    }

    /// <summary>
    /// True if the AR (12010) or AP (21010) control account already carries a manual lump-sum
    /// entry from the Opening Balances screen — importing invoices on top of that would double
    /// count the balance, since each imported invoice posts its own Dr/Cr against the same
    /// control account. The import dialogs show this as a warning (not a hard block, since
    /// clearing that manual entry first is a one-click fix on the Opening Balances screen).
    /// </summary>
    public async Task<bool> HasManualControlAccountOpeningAsync(string accountCode)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.JournalVouchers.AsNoTracking()
            .AnyAsync(v => v.Type == VoucherType.Opening && v.Reference == accountCode);
    }

    public async Task<List<ImportInvoiceRowResult>> ImportSalesInvoicesAsync(
        List<ImportInvoiceRow> rows, string importedBy)
    {
        var now = DateTime.UtcNow;
        var results = new List<ImportInvoiceRowResult>();
        var rowNo = 1; // row 1 is the header line in the source file
        foreach (var row in rows)
        {
            rowNo++;
            try
            {
                if (string.IsNullOrWhiteSpace(row.PartyName))
                    throw new PostingException("Customer name is required.");

                var customer = await FindOrCreateCustomerAsync(row.PartyName.Trim(), importedBy, now);
                var invoice = await _salesInvoices.ImportOpeningInvoiceAsync(
                    customer.Id, row.Reference, row.Date, row.DueDate, row.Amount, row.Notes, importedBy, now);
                results.Add(new ImportInvoiceRowResult(rowNo, row.PartyName, true,
                    $"Imported as {invoice.InvoiceNo} — {customer.Name}"));
            }
            catch (PostingException ex)
            {
                results.Add(new ImportInvoiceRowResult(rowNo, row.PartyName, false, ex.Message));
            }
        }
        return results;
    }

    public async Task<List<ImportInvoiceRowResult>> ImportPurchaseInvoicesAsync(
        List<ImportInvoiceRow> rows, string importedBy)
    {
        var now = DateTime.UtcNow;
        var results = new List<ImportInvoiceRowResult>();
        var rowNo = 1;
        foreach (var row in rows)
        {
            rowNo++;
            try
            {
                if (string.IsNullOrWhiteSpace(row.PartyName))
                    throw new PostingException("Vendor name is required.");

                var vendor = await FindOrCreateVendorAsync(row.PartyName.Trim(), importedBy, now);
                var invoice = await _purchaseInvoices.ImportOpeningInvoiceAsync(
                    vendor.Id, row.Reference, row.Date, row.DueDate, row.Amount, row.Notes, importedBy, now);
                results.Add(new ImportInvoiceRowResult(rowNo, row.PartyName, true,
                    $"Imported as {invoice.InvoiceNo} — {vendor.Name}"));
            }
            catch (PostingException ex)
            {
                results.Add(new ImportInvoiceRowResult(rowNo, row.PartyName, false, ex.Message));
            }
        }
        return results;
    }

    /// <summary>Matches an existing customer by exact (case-insensitive, trimmed) name, or creates
    /// a bare-minimum one — just the name, AED currency, no credit limit — so an import never
    /// fails just because the old system's client isn't in Aegis ERP yet. The new record can be
    /// filled in properly (TRN, address, contact) later from the Customers page.</summary>
    private async Task<Customer> FindOrCreateCustomerAsync(string name, string importedBy, DateTime now)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
        if (existing is not null) return existing;

        return await _customers.CreateAsync(
            new NewCustomerInput(name, null, "AED", 0, 30, null, null, null, null),
            changedBy: importedBy, nowUtc: now);
    }

    private async Task<Vendor> FindOrCreateVendorAsync(string name, string importedBy, DateTime now)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.Vendors.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Name.ToLower() == name.ToLower());
        if (existing is not null) return existing;

        return await _vendors.CreateAsync(new NewVendorInput(name, null, "AED", 0, 30, null, null, null, null));
    }
}
