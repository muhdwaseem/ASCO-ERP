using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One billing or shipping address block — same shape reused for both. Mirrors
/// <see cref="CustomerAddressInput"/>.</summary>
public record VendorAddressInput(
    string? Attention, string? Country, string? AddressLine1, string? AddressLine1Arabic,
    string? AddressLine2, string? AddressLine2Arabic, string? City, string? Emirate,
    string? Zip, string? Phone, string? Fax);

/// <summary>Metadata for one attached document — never carries the file bytes themselves (use
/// <see cref="VendorService.GetDocumentAsync"/> to download). Mirrors <see cref="CustomerDocumentInfo"/>.</summary>
public record VendorDocumentInfo(int Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAtUtc);

/// <summary>A purchase invoice with its remaining balance and a Zoho-style display status — the AP
/// mirror of <see cref="SalesInvoiceRow"/>, backing the "Ask AI" bills_by_status/overdue_bills
/// intents. Status is one of Draft/Pending/Overdue/Paid/Void, same vocabulary as <see cref="ArStatus"/>
/// even though purchase invoices don't carry that enum themselves.</summary>
public record PurchaseInvoiceStatusRow(
    int InvoiceId, string VendorCode, string VendorName, string InvoiceNo,
    DateOnly Date, DateOnly DueDate, decimal Gross, decimal Balance, string Status);

/// <summary>Everything the "New Vendor" / "Edit Vendor" form collects, aside from documents
/// (added/removed after the vendor exists — see <see cref="VendorService.AddDocumentAsync"/>).
/// Field set mirrors <see cref="NewCustomerInput"/> minus the AR-only concepts (Salesperson,
/// EnablePortal) that have no AP equivalent.</summary>
public record NewVendorInput(
    string Name, string? Group, string Currency, decimal CreditLimit,
    int PaymentTermsDays, string? Trn, string? Email, string? Phone, string? Address,
    CustomerType VendorType = CustomerType.Business,
    string? Salutation = null, string? FirstName = null, string? LastName = null, string? CompanyName = null,
    string? DisplayNameArabic = null, string VendorLanguage = "English",
    string? WorkPhone = null, string? Mobile = null,
    TaxTreatment? TaxTreatment = null, string? PlaceOfSupply = null, decimal OpeningBalance = 0,
    string? Remarks = null,
    VendorAddressInput? Billing = null, VendorAddressInput? Shipping = null);

public class VendorService
{
    /// <summary>Custom Fields / Reporting Tags module key this form uses.</summary>
    public const string Module = "Vendor";

    public const int MaxDocumentBytes = 10 * 1024 * 1024;
    public const int MaxDocumentsPerVendor = 10;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public VendorService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<Vendor>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Vendors.AsNoTracking().OrderBy(v => v.Code).AsQueryable();
        if (activeOnly) q = q.Where(v => v.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>One vendor with full detail for the vendor detail view / Edit dialog — contact
    /// persons, custom field values and tag selections. Documents are deliberately excluded (see
    /// <see cref="GetDocumentsAsync"/>) so this never pulls file bytes into memory.</summary>
    public async Task<Vendor?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Vendors.AsNoTracking()
            .Include(v => v.ContactPersons)
            .Include(v => v.CustomFieldValues).ThenInclude(cf => cf.CustomFieldDefinition)
            .Include(v => v.Tags).ThenInclude(t => t.Tag).ThenInclude(t => t.TagGroup)
            .FirstOrDefaultAsync(v => v.Id == id);
    }

    public async Task<Vendor> CreateAsync(
        NewVendorInput input,
        IEnumerable<ContactPersonInput>? contactPersons = null,
        IEnumerable<CustomFieldValueInput>? customFieldValues = null,
        IEnumerable<int>? tagIds = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateHeader(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var vendor = new Vendor { Code = await NextCodeAsync(db), CreatedAtUtc = DateTime.UtcNow };
        ApplyScalarFields(vendor, input);
        foreach (var p in BuildContactPersons(contactPersons ?? Enumerable.Empty<ContactPersonInput>()))
            vendor.ContactPersons.Add(p);
        await ValidateAndBuildCustomFieldValuesAsync(db, customFieldValues ?? Enumerable.Empty<CustomFieldValueInput>(), vendor.CustomFieldValues);
        await ValidateAndBuildTagsAsync(db, tagIds ?? Enumerable.Empty<int>(), vendor.Tags);

        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        return vendor;
    }

    /// <summary>Updates an existing vendor's fields, contact persons, custom field values and tag
    /// selections — each child collection is fully replaced with what's submitted (the form always
    /// sends its complete current state, not a diff).</summary>
    public async Task UpdateAsync(
        int id, NewVendorInput input,
        IEnumerable<ContactPersonInput>? contactPersons = null,
        IEnumerable<CustomFieldValueInput>? customFieldValues = null,
        IEnumerable<int>? tagIds = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateHeader(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var vendor = await db.Vendors
            .Include(v => v.ContactPersons).Include(v => v.CustomFieldValues).Include(v => v.Tags)
            .FirstOrDefaultAsync(v => v.Id == id)
            ?? throw new PostingException("Vendor not found.");

        ApplyScalarFields(vendor, input);

        vendor.ContactPersons.Clear();
        foreach (var p in BuildContactPersons(contactPersons ?? Enumerable.Empty<ContactPersonInput>()))
            vendor.ContactPersons.Add(p);

        vendor.CustomFieldValues.Clear();
        await ValidateAndBuildCustomFieldValuesAsync(db, customFieldValues ?? Enumerable.Empty<CustomFieldValueInput>(), vendor.CustomFieldValues);

        vendor.Tags.Clear();
        await ValidateAndBuildTagsAsync(db, tagIds ?? Enumerable.Empty<int>(), vendor.Tags);

        await db.SaveChangesAsync();
    }

    private static void ValidateHeader(NewVendorInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Vendor name is required.");
        if (input.PaymentTermsDays < 0) throw new PostingException("Payment terms cannot be negative.");
        if (input.CreditLimit < 0) throw new PostingException("Credit limit cannot be negative.");
        if (input.OpeningBalance < 0) throw new PostingException("Opening balance cannot be negative.");
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void ApplyScalarFields(Vendor v, NewVendorInput input)
    {
        v.VendorType = input.VendorType;
        v.Salutation = Trim(input.Salutation);
        v.FirstName = Trim(input.FirstName);
        v.LastName = Trim(input.LastName);
        v.CompanyName = Trim(input.CompanyName);
        v.Name = input.Name.Trim();
        v.DisplayNameArabic = Trim(input.DisplayNameArabic);
        v.VendorLanguage = string.IsNullOrWhiteSpace(input.VendorLanguage) ? "English" : input.VendorLanguage.Trim();
        v.Trn = Trim(input.Trn);
        v.Email = Trim(input.Email);
        v.Phone = Trim(input.Phone);
        v.WorkPhone = Trim(input.WorkPhone);
        v.Mobile = Trim(input.Mobile);
        v.Address = Trim(input.Address);
        v.Group = Trim(input.Group);
        v.Currency = string.IsNullOrWhiteSpace(input.Currency) ? "AED" : input.Currency.Trim();
        v.CreditLimit = input.CreditLimit;
        v.PaymentTermsDays = input.PaymentTermsDays;

        v.TaxTreatment = input.TaxTreatment;
        v.PlaceOfSupply = Trim(input.PlaceOfSupply);
        v.OpeningBalance = input.OpeningBalance;
        v.Remarks = Trim(input.Remarks);

        var billing = input.Billing;
        v.BillingAttention = Trim(billing?.Attention);
        v.BillingCountry = Trim(billing?.Country);
        v.BillingAddressLine1 = Trim(billing?.AddressLine1);
        v.BillingAddressLine1Arabic = Trim(billing?.AddressLine1Arabic);
        v.BillingAddressLine2 = Trim(billing?.AddressLine2);
        v.BillingAddressLine2Arabic = Trim(billing?.AddressLine2Arabic);
        v.BillingCity = Trim(billing?.City);
        v.BillingEmirate = Trim(billing?.Emirate);
        v.BillingZip = Trim(billing?.Zip);
        v.BillingPhone = Trim(billing?.Phone);
        v.BillingFax = Trim(billing?.Fax);

        var shipping = input.Shipping;
        v.ShippingAttention = Trim(shipping?.Attention);
        v.ShippingCountry = Trim(shipping?.Country);
        v.ShippingAddressLine1 = Trim(shipping?.AddressLine1);
        v.ShippingAddressLine1Arabic = Trim(shipping?.AddressLine1Arabic);
        v.ShippingAddressLine2 = Trim(shipping?.AddressLine2);
        v.ShippingAddressLine2Arabic = Trim(shipping?.AddressLine2Arabic);
        v.ShippingCity = Trim(shipping?.City);
        v.ShippingEmirate = Trim(shipping?.Emirate);
        v.ShippingZip = Trim(shipping?.Zip);
        v.ShippingPhone = Trim(shipping?.Phone);
        v.ShippingFax = Trim(shipping?.Fax);
    }

    private static List<VendorContactPerson> BuildContactPersons(IEnumerable<ContactPersonInput> inputs)
    {
        var list = new List<VendorContactPerson>();
        var primaryCount = 0;
        foreach (var i in inputs)
        {
            if (string.IsNullOrWhiteSpace(i.FirstName))
                throw new PostingException("Each contact person needs a first name.");
            if (i.IsPrimary) primaryCount++;
            if (primaryCount > 1)
                throw new PostingException("Only one contact person can be marked as primary.");

            list.Add(new VendorContactPerson
            {
                Salutation = Trim(i.Salutation),
                FirstName = i.FirstName.Trim(),
                LastName = Trim(i.LastName),
                Email = Trim(i.Email),
                WorkPhone = Trim(i.WorkPhone),
                Mobile = Trim(i.Mobile),
                Designation = Trim(i.Designation),
                Department = Trim(i.Department),
                IsPrimary = i.IsPrimary,
            });
        }
        return list;
    }

    /// <summary>Every active custom field defined for the Vendor module must have a value if
    /// marked required, and a Dropdown field's value must be one of its own options.</summary>
    private static async Task ValidateAndBuildCustomFieldValuesAsync(
        AegisDbContext db, IEnumerable<CustomFieldValueInput> inputs, List<VendorCustomFieldValue> target)
    {
        var defs = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(f => f.Module == Module && f.IsActive).ToListAsync();
        var defById = defs.ToDictionary(f => f.Id);
        var provided = inputs
            .Where(i => !string.IsNullOrWhiteSpace(i.Value))
            .ToDictionary(i => i.CustomFieldDefinitionId, i => i.Value!.Trim());

        foreach (var def in defs)
        {
            var hasValue = provided.TryGetValue(def.Id, out var value);
            if (def.IsRequired && !hasValue)
                throw new PostingException($"'{def.Label}' is required.");
            if (hasValue && def.FieldType == CustomFieldType.Dropdown)
            {
                var options = (def.DropdownOptionsCsv ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!options.Contains(value, StringComparer.OrdinalIgnoreCase))
                    throw new PostingException($"'{value}' is not a valid option for '{def.Label}'.");
            }
        }

        foreach (var (defId, value) in provided)
        {
            if (!defById.ContainsKey(defId)) continue; // stale/inactive definition — ignore silently
            target.Add(new VendorCustomFieldValue { CustomFieldDefinitionId = defId, Value = value });
        }
    }

    /// <summary>At most one tag per tag group.</summary>
    private static async Task ValidateAndBuildTagsAsync(AegisDbContext db, IEnumerable<int> tagIds, List<VendorTag> target)
    {
        var ids = tagIds.Distinct().ToList();
        if (ids.Count == 0) return;

        // Tag has no CompanyId/query filter of its own — route through the company-scoped
        // TagGroups so a tagId belonging to another company can never be attached here.
        var tags = await db.TagGroups.AsNoTracking().SelectMany(g => g.Tags)
            .Where(t => ids.Contains(t.Id)).ToListAsync();
        var groupsSeen = new HashSet<int>();
        foreach (var t in tags)
        {
            if (!groupsSeen.Add(t.TagGroupId))
                throw new PostingException("Only one tag can be selected per tag group.");
            target.Add(new VendorTag { TagId = t.Id });
        }
    }

    // ── Documents ──

    public async Task<List<VendorDocumentInfo>> GetDocumentsAsync(int vendorId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.Vendors.FindAsync(vendorId) ?? throw new PostingException("Vendor not found.");

        return await db.VendorDocuments.AsNoTracking()
            .Where(d => d.VendorId == vendorId)
            .OrderBy(d => d.Id)
            .Select(d => new VendorDocumentInfo(d.Id, d.FileName, d.ContentType, d.Data.Length, d.UploadedAtUtc))
            .ToListAsync();
    }

    public async Task<VendorDocument> AddDocumentAsync(int vendorId, string fileName, string contentType, byte[] data, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (data.Length > MaxDocumentBytes)
            throw new PostingException($"'{fileName}' is too large — the limit is {MaxDocumentBytes / (1024 * 1024)} MB per file.");

        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.Vendors.FindAsync(vendorId) ?? throw new PostingException("Vendor not found.");

        var count = await db.VendorDocuments.CountAsync(d => d.VendorId == vendorId);
        if (count >= MaxDocumentsPerVendor)
            throw new PostingException($"A vendor can have at most {MaxDocumentsPerVendor} documents attached.");

        var doc = new VendorDocument
        {
            VendorId = vendorId,
            FileName = fileName,
            ContentType = contentType,
            Data = data,
            UploadedAtUtc = nowUtc,
        };
        db.VendorDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    public async Task RemoveDocumentAsync(int vendorId, int documentId)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.Vendors.FindAsync(vendorId) ?? throw new PostingException("Vendor not found.");

        var doc = await db.VendorDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.VendorId == vendorId)
            ?? throw new PostingException("Document not found.");
        db.VendorDocuments.Remove(doc);
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetDocumentAsync(int vendorId, int documentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Vendors.FindAsync(vendorId) is null) return null;

        var doc = await db.VendorDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.VendorId == vendorId);
        return doc is null ? null : (doc.FileName, doc.ContentType, doc.Data);
    }

    /// <summary>The next vendor code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await NextCodeAsync(db);
    }

    private static async Task<string> NextCodeAsync(AegisDbContext db)
    {
        var codes = await db.Vendors.Select(v => v.Code).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("V-") && int.TryParse(code.AsSpan(2), out var n) && n > max)
                max = n;
        return $"V-{max + 1:0000}";
    }

    /// <summary>All vendors with billed / paid / outstanding totals from posted documents.</summary>
    public async Task<List<VendorSummary>> GetSummariesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Code).ToListAsync();
        var invoices = await PostedInvoicesAsync(db);
        var payments = await PostedPaymentsAsync(db);
        var debits = await PostedDebitNotesAsync(db);

        return vendors.Select(v =>
        {
            var billed = invoices.Where(i => i.VendorId == v.Id).Sum(i => i.TotalGross);
            var paid = payments.Where(p => p.VendorId == v.Id).Sum(p => p.Amount)
                       + debits.Where(d => d.VendorId == v.Id).Sum(d => d.TotalGross);
            return new VendorSummary(v.Id, v.Code, v.Name, v.Trn, v.PaymentTermsDays,
                billed, paid, billed - paid);
        }).ToList();
    }

    /// <summary>Chronological purchase-invoice / payment / debit-note statement with running balance (what we owe).</summary>
    public async Task<List<StatementRow>> GetStatementAsync(int vendorId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.VendorId == vendorId);
        var payments = (await PostedPaymentsAsync(db)).Where(p => p.VendorId == vendorId);
        var debits = (await PostedDebitNotesAsync(db)).Where(d => d.VendorId == vendorId);

        // Credit = we owe more (invoice); Debit = we owe less (payment / debit note).
        var rows = invoices
            .Select(i => (i.Date, DocNo: i.InvoiceNo, DocType: "Purchase Invoice",
                Narration: i.Narration ?? "", Debit: 0m, Credit: i.TotalGross))
            .Concat(payments.Select(p => (p.Date, DocNo: p.PaymentNo, DocType: "Payment",
                Narration: p.Narration ?? "", Debit: p.Amount, Credit: 0m)))
            .Concat(debits.Select(d => (d.Date, DocNo: d.DebitNoteNo, DocType: "Debit Note",
                Narration: d.Narration ?? d.Reason ?? "", Debit: d.TotalGross, Credit: 0m)))
            .OrderBy(r => r.Date).ThenBy(r => r.DocNo)
            .ToList();

        var result = new List<StatementRow>();
        var running = 0m;
        foreach (var r in rows)
        {
            running += r.Credit - r.Debit; // payable grows on credit
            result.Add(new StatementRow(r.Date, r.DocNo, r.DocType, r.Narration, r.Debit, r.Credit, running));
        }
        return result;
    }

    /// <summary>
    /// AP aging as of a date. Each open purchase invoice's outstanding (gross − allocated payments −
    /// allocated debit notes) is bucketed by days past its due date; unallocated payments/debit notes
    /// appear as debits.
    /// </summary>
    public async Task<List<ApAgingRow>> GetAgingAsync(DateOnly asOf)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Code).ToListAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.Date <= asOf).ToList();
        var payments = (await PostedPaymentsAsync(db)).Where(p => p.Date <= asOf).ToList();
        var debits = (await PostedDebitNotesAsync(db)).Where(d => d.Date <= asOf).ToList();
        var (allocatedByInvoice, paymentIdsWithAllocations) = await AllocationsForAsync(db, payments.Select(p => p.Id).ToList());

        var rows = new List<ApAgingRow>();
        foreach (var v in vendors)
        {
            decimal current = 0, b30 = 0, b60 = 0, b90 = 0, over = 0;
            foreach (var inv in invoices.Where(i => i.VendorId == v.Id))
            {
                var allocated = allocatedByInvoice.GetValueOrDefault(inv.Id)
                                + debits.Where(d => d.PurchaseInvoiceId == inv.Id).Sum(d => d.TotalGross);
                var outstanding = inv.TotalGross - allocated;
                if (outstanding <= 0) continue;

                var daysPastDue = asOf.DayNumber - inv.DueDate.DayNumber;
                if (daysPastDue <= 0) current += outstanding;
                else if (daysPastDue <= 30) b30 += outstanding;
                else if (daysPastDue <= 60) b60 += outstanding;
                else if (daysPastDue <= 90) b90 += outstanding;
                else over += outstanding;
            }

            // A payment is either 100% on-account (no allocations at all) or 100% applied (its
            // allocations sum to exactly its Amount — no partial/overpayment state is allowed),
            // so "unallocated" is simply "has zero allocation rows", not merely PurchaseInvoiceId
            // == null (a payment spanning multiple invoices also has PurchaseInvoiceId == null
            // but is fully applied).
            var unallocated = payments
                .Where(p => p.VendorId == v.Id && !paymentIdsWithAllocations.Contains(p.Id))
                .Sum(p => p.Amount)
                + debits.Where(d => d.VendorId == v.Id && d.PurchaseInvoiceId == null).Sum(d => d.TotalGross);

            if (current + b30 + b60 + b90 + over > 0 || unallocated > 0)
                rows.Add(new ApAgingRow(v.Code, v.Name, current, b30, b60, b90, over, unallocated));
        }
        return rows;
    }

    /// <summary>
    /// Every purchase invoice (any status), firm-wide, with its remaining balance and computed
    /// display status — the AP mirror of <see cref="SalesInvoiceService.GetAllAsync"/>. Backs the
    /// "Ask AI" bills_by_status and overdue_bills intents; nothing else in the app currently needs a
    /// firm-wide bill list with status, so this exists purely for that.
    /// </summary>
    public async Task<List<PurchaseInvoiceStatusRow>> GetAllWithStatusAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendors = await db.Vendors.AsNoTracking().ToDictionaryAsync(v => v.Id);
        var invoices = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines).ToListAsync();
        var payments = await PostedPaymentsAsync(db);
        var debits = await PostedDebitNotesAsync(db);
        var (allocatedByInvoice, _) = await AllocationsForAsync(db, payments.Select(p => p.Id).ToList());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = new List<PurchaseInvoiceStatusRow>();
        foreach (var inv in invoices)
        {
            if (!vendors.TryGetValue(inv.VendorId, out var vendor)) continue;

            var allocated = allocatedByInvoice.GetValueOrDefault(inv.Id)
                            + debits.Where(d => d.PurchaseInvoiceId == inv.Id).Sum(d => d.TotalGross);
            var balance = inv.TotalGross - allocated;
            var status = inv.Status switch
            {
                VoucherStatus.Draft => "Draft",
                VoucherStatus.Void => "Void",
                _ => balance <= 0 ? "Paid" : inv.DueDate < today ? "Overdue" : "Pending",
            };
            rows.Add(new PurchaseInvoiceStatusRow(inv.Id, vendor.Code, vendor.Name, inv.InvoiceNo,
                inv.Date, inv.DueDate, inv.TotalGross, balance, status));
        }
        return rows.OrderByDescending(r => r.Date).ToList();
    }

    /// <summary>Posted purchase invoices for a vendor that still have money owing.</summary>
    public async Task<List<OpenPurchaseInvoice>> GetOpenInvoicesAsync(int vendorId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.VendorId == vendorId).ToList();
        var payments = (await PostedPaymentsAsync(db)).Where(p => p.VendorId == vendorId).ToList();
        var debits = (await PostedDebitNotesAsync(db)).Where(d => d.VendorId == vendorId).ToList();
        var (allocatedByInvoice, _) = await AllocationsForAsync(db, payments.Select(p => p.Id).ToList());

        return invoices
            .Select(i =>
            {
                var allocated = allocatedByInvoice.GetValueOrDefault(i.Id)
                                + debits.Where(d => d.PurchaseInvoiceId == i.Id).Sum(d => d.TotalGross);
                var billableLineCount = i.Lines.Count(l => l.Net > 0);
                return new OpenPurchaseInvoice(i.Id, i.InvoiceNo, i.Date, i.DueDate, i.TotalGross, i.TotalGross - allocated, billableLineCount);
            })
            .Where(o => o.Outstanding > 0)
            .OrderBy(o => o.Date)
            .ToList();
    }

    private static Task<List<PurchaseInvoice>> PostedInvoicesAsync(AegisDbContext db) =>
        db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<VendorPayment>> PostedPaymentsAsync(AegisDbContext db) =>
        db.VendorPayments.AsNoTracking()
            .Where(p => p.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<DebitNote>> PostedDebitNotesAsync(AegisDbContext db) =>
        db.DebitNotes.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.Status == VoucherStatus.Posted).ToListAsync();

    /// <summary>
    /// For a set of (already Posted-filtered) payment ids: how much is allocated to each invoice
    /// they touch, and which of those payment ids have any allocation at all (vs. purely on
    /// account). A payment's <see cref="VendorPayment.PurchaseInvoiceId"/> is never used here — it
    /// can't represent a payment spanning multiple invoices, so <see cref="VendorPaymentAllocation"/>
    /// (joined through the line it targets) is the only correct source of truth.
    /// </summary>
    private static async Task<(Dictionary<int, decimal> AllocatedByInvoice, HashSet<int> PaymentIdsWithAllocations)>
        AllocationsForAsync(AegisDbContext db, List<int> paymentIds)
    {
        var allocations = await db.VendorPaymentAllocations.AsNoTracking()
            .Where(a => paymentIds.Contains(a.VendorPaymentId))
            .Select(a => new { a.VendorPaymentId, InvoiceId = a.PurchaseInvoiceLine.PurchaseInvoiceId, a.Amount })
            .ToListAsync();

        var byInvoice = allocations.GroupBy(a => a.InvoiceId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var paymentsWithAllocations = allocations.Select(a => a.VendorPaymentId).ToHashSet();
        return (byInvoice, paymentsWithAllocations);
    }
}
