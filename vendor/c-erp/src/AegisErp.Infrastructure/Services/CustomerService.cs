using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One billing or shipping address block — same shape reused for both.</summary>
public record CustomerAddressInput(
    string? Attention, string? Country, string? AddressLine1, string? AddressLine1Arabic,
    string? AddressLine2, string? AddressLine2Arabic, string? City, string? Emirate,
    string? Zip, string? Phone, string? Fax);

/// <summary>One person to contact at the customer.</summary>
public record ContactPersonInput(
    string? Salutation, string FirstName, string? LastName, string? Email,
    string? WorkPhone, string? Mobile, string? Designation, string? Department, bool IsPrimary);

/// <summary>One of the customer's separate trade licenses / legal entities (see
/// <see cref="AegisErp.Domain.Entities.CustomerOrganization"/>).</summary>
public record OrganizationInput(string Name, string? Trn);

/// <summary>One admin-defined custom field's value for this customer. A blank/whitespace
/// <paramref name="Value"/> is treated as "not answered", not as the literal empty string.</summary>
public record CustomFieldValueInput(int CustomFieldDefinitionId, string? Value);

/// <summary>Metadata for one attached document — never carries the file bytes themselves (use
/// <see cref="CustomerService.GetDocumentAsync"/> to download).</summary>
public record CustomerDocumentInfo(int Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAtUtc);

/// <summary>Everything the "New Customer" / "Edit Customer" form collects, aside from documents
/// (added/removed after the customer exists — see <see cref="CustomerService.AddDocumentAsync"/>).</summary>
public record NewCustomerInput(
    string Name, string? Group, string Currency, decimal CreditLimit,
    int PaymentTermsDays, string? Trn, string? Email, string? Phone, string? Address,
    string? Salesperson = null,
    int? AssignedEmployeeId = null, int? AssignedAgentId = null,
    CustomerType CustomerType = CustomerType.Business,
    string? Salutation = null, string? FirstName = null, string? LastName = null, string? CompanyName = null,
    string? DisplayNameArabic = null, string CustomerLanguage = "English",
    string? WorkPhone = null, string? Mobile = null,
    TaxTreatment? TaxTreatment = null, string? PlaceOfSupply = null, decimal OpeningBalance = 0,
    bool EnablePortal = false, string? Remarks = null,
    CustomerAddressInput? Billing = null, CustomerAddressInput? Shipping = null);

/// <summary>One open invoice for the firm-wide Outstanding Report — see
/// <see cref="CustomerService.GetOutstandingInvoicesAsync"/>.</summary>
public record OutstandingInvoiceRow(
    int InvoiceId, string CustomerCode, string CustomerName, string InvoiceNo,
    DateOnly Date, DateOnly DueDate, decimal AmountDue, int DaysOverdue,
    int? EmployeeId, string? EmployeeName, int? AgentId, string? AgentName);

public class CustomerService
{
    /// <summary>Custom Fields / Reporting Tags module key this form uses — kept as a constant so
    /// both subsystems can extend to other modules later without touching this string in two places.</summary>
    public const string Module = "Customer";

    public const int MaxDocumentBytes = 10 * 1024 * 1024;
    public const int MaxDocumentsPerCustomer = 10;

    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public CustomerService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<Customer>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Customers.AsNoTracking().OrderBy(c => c.Code).AsQueryable();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>One customer with full detail for the Edit dialog — contact persons, custom field
    /// values and tag selections. Documents are deliberately excluded (see
    /// <see cref="GetDocumentsAsync"/>) so this never pulls file bytes into memory.</summary>
    public async Task<Customer?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Customers.AsNoTracking()
            .Include(c => c.ContactPersons)
            .Include(c => c.CustomFieldValues).ThenInclude(v => v.CustomFieldDefinition)
            .Include(c => c.Tags).ThenInclude(t => t.Tag).ThenInclude(t => t.TagGroup)
            .Include(c => c.Organizations)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Customer> CreateAsync(
        NewCustomerInput input,
        IEnumerable<ContactPersonInput>? contactPersons = null,
        IEnumerable<CustomFieldValueInput>? customFieldValues = null,
        IEnumerable<int>? tagIds = null,
        string changedBy = "System Admin",
        DateTime? nowUtc = null,
        IEnumerable<OrganizationInput>? organizations = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateHeader(input);

        await using var db = await _dbf.CreateDbContextAsync();

        // Next code = max numeric suffix + 1 (seed uses C-0001..C-0003).
        var codes = await db.Customers.Select(c => c.Code).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("C-") && int.TryParse(code.AsSpan(2), out var n) && n > max)
                max = n;

        var customer = new Customer { Code = $"C-{max + 1:0000}", CreatedAtUtc = nowUtc ?? DateTime.UtcNow };
        LogSalespersonChangeIfNeeded(customer, input.Salesperson, changedBy, nowUtc ?? DateTime.UtcNow);
        ReassignEmployeeAgentIfNeeded(customer, input.AssignedEmployeeId, input.AssignedAgentId,
            DateOnly.FromDateTime(nowUtc ?? DateTime.UtcNow), changedBy, nowUtc ?? DateTime.UtcNow);
        ApplyScalarFields(customer, input);
        foreach (var p in BuildContactPersons(contactPersons ?? Enumerable.Empty<ContactPersonInput>()))
            customer.ContactPersons.Add(p);
        await ValidateAndBuildCustomFieldValuesAsync(db, customFieldValues ?? Enumerable.Empty<CustomFieldValueInput>(), customer.CustomFieldValues);
        await ValidateAndBuildTagsAsync(db, tagIds ?? Enumerable.Empty<int>(), customer.Tags);
        foreach (var o in BuildOrganizations(organizations ?? Enumerable.Empty<OrganizationInput>()))
            customer.Organizations.Add(o);

        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>Updates an existing customer's fields, contact persons, custom field values, tag
    /// selections and organizations — each child collection is fully replaced with what's submitted
    /// (the form always sends its complete current state, not a diff).</summary>
    public async Task UpdateAsync(
        int id, NewCustomerInput input,
        IEnumerable<ContactPersonInput>? contactPersons = null,
        IEnumerable<CustomFieldValueInput>? customFieldValues = null,
        IEnumerable<int>? tagIds = null,
        string changedBy = "System Admin",
        DateTime? nowUtc = null,
        IEnumerable<OrganizationInput>? organizations = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        ValidateHeader(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers
            .Include(c => c.ContactPersons).Include(c => c.CustomFieldValues).Include(c => c.Tags)
            .Include(c => c.Organizations).Include(c => c.AssignmentHistory)
            .FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Customer not found.");

        LogSalespersonChangeIfNeeded(customer, input.Salesperson, changedBy, nowUtc ?? DateTime.UtcNow);
        ReassignEmployeeAgentIfNeeded(customer, input.AssignedEmployeeId, input.AssignedAgentId,
            DateOnly.FromDateTime(nowUtc ?? DateTime.UtcNow), changedBy, nowUtc ?? DateTime.UtcNow);
        ApplyScalarFields(customer, input);

        customer.ContactPersons.Clear();
        foreach (var p in BuildContactPersons(contactPersons ?? Enumerable.Empty<ContactPersonInput>()))
            customer.ContactPersons.Add(p);

        customer.CustomFieldValues.Clear();
        await ValidateAndBuildCustomFieldValuesAsync(db, customFieldValues ?? Enumerable.Empty<CustomFieldValueInput>(), customer.CustomFieldValues);

        customer.Tags.Clear();
        await ValidateAndBuildTagsAsync(db, tagIds ?? Enumerable.Empty<int>(), customer.Tags);

        customer.Organizations.Clear();
        foreach (var o in BuildOrganizations(organizations ?? Enumerable.Empty<OrganizationInput>()))
            customer.Organizations.Add(o);

        await db.SaveChangesAsync();
    }

    /// <summary>Ordered history of this customer's salesperson reassignments (oldest first), for an
    /// audit-trail display and for time-correct revenue attribution reports. Goes through Customers
    /// (company-scoped) rather than querying SalespersonAssignmentHistories directly, since the
    /// history row itself carries no CompanyId — same "scope through the parent" pattern
    /// PayrollService.GetRunLineAsync uses for PayrollRunLine.</summary>
    public async Task<List<SalespersonAssignmentHistory>> GetSalespersonHistoryAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Customers.AsNoTracking().SelectMany(c => c.SalespersonHistory)
            .Where(h => h.CustomerId == customerId)
            .OrderBy(h => h.ChangedAtUtc).ThenBy(h => h.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Appends a <see cref="SalespersonAssignmentHistory"/> row when <paramref name="newValue"/>
    /// (after trimming) actually differs from the customer's current <see cref="Customer.Salesperson"/>
    /// — including the very first assignment (<c>customer.Salesperson</c> is still null/default at
    /// this point, since this runs before <see cref="ApplyScalarFields"/>), so a report built on this
    /// history never has to special-case "no history exists yet."
    /// </summary>
    private static void LogSalespersonChangeIfNeeded(Customer customer, string? newValue, string changedBy, DateTime nowUtc)
    {
        var previous = customer.Salesperson;
        var next = Trim(newValue);
        if (string.Equals(previous, next, StringComparison.Ordinal)) return;

        customer.SalespersonHistory.Add(new SalespersonAssignmentHistory
        {
            PreviousSalesperson = previous,
            NewSalesperson = next,
            ChangedAtUtc = nowUtc,
            ChangedBy = string.IsNullOrWhiteSpace(changedBy) ? "System Admin" : changedBy.Trim(),
        });
    }

    /// <summary>Ordered history of this customer's Employee/Agent commission assignments (oldest
    /// first) — see <see cref="CustomerAssignmentHistory"/>. Same "scope through the parent" pattern
    /// as <see cref="GetSalespersonHistoryAsync"/>.</summary>
    public async Task<List<CustomerAssignmentHistory>> GetAssignmentHistoryAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Customers.AsNoTracking().Where(c => c.Id == customerId)
            .SelectMany(c => c.AssignmentHistory)
            .Include(h => h.Employee).Include(h => h.Agent)
            .OrderBy(h => h.EffectiveFrom).ThenBy(h => h.Id)
            .ToListAsync();
    }

    /// <summary>Explicitly reassigns a customer's commission Employee/Agent as of
    /// <paramref name="cutoffDate"/> — for a deliberate reassignment separate from the general Edit
    /// Customer form (which reassigns effective "today" via <see cref="UpdateAsync"/>). Lets a caller
    /// backdate/forward-date a cut-off when the business needs one.</summary>
    public async Task ReassignEmployeeAgentAsync(int customerId, int? newEmployeeId, int? newAgentId,
        DateOnly cutoffDate, string changedBy, DateTime? nowUtc = null)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers.Include(c => c.AssignmentHistory).FirstOrDefaultAsync(c => c.Id == customerId)
            ?? throw new PostingException("Customer not found.");

        ReassignEmployeeAgentIfNeeded(customer, newEmployeeId, newAgentId, cutoffDate, changedBy, nowUtc ?? DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Closes the customer's current open <see cref="CustomerAssignmentHistory"/> row
    /// (<see cref="CustomerAssignmentHistory.EffectiveTo"/> = <paramref name="cutoffDate"/>) and opens
    /// a new one (<see cref="CustomerAssignmentHistory.EffectiveFrom"/> = the same date), then updates
    /// the customer's own <see cref="Customer.AssignedEmployeeId"/>/<see cref="Customer.AssignedAgentId"/>
    /// to match — mirrors <see cref="LogSalespersonChangeIfNeeded"/>'s "diff before overwrite" shape,
    /// except this is a true interval close/open rather than a flat append, since transaction
    /// attribution needs to know exactly when an assignment stopped applying (see Section 4 of the
    /// Commission Workflow spec). A no-op when neither value actually changes — including the very
    /// first save where both are left unset, so a customer with no Employee/Agent never gets a
    /// meaningless history row.
    /// </summary>
    private static void ReassignEmployeeAgentIfNeeded(Customer customer, int? newEmployeeId, int? newAgentId,
        DateOnly cutoffDate, string changedBy, DateTime nowUtc)
    {
        if (customer.AssignedEmployeeId == newEmployeeId && customer.AssignedAgentId == newAgentId) return;

        var open = customer.AssignmentHistory.FirstOrDefault(h => h.EffectiveTo == null);
        if (open is not null) open.EffectiveTo = cutoffDate;

        customer.AssignmentHistory.Add(new CustomerAssignmentHistory
        {
            EmployeeId = newEmployeeId,
            AgentId = newAgentId,
            EffectiveFrom = cutoffDate,
            EffectiveTo = null,
            ChangedBy = string.IsNullOrWhiteSpace(changedBy) ? "System Admin" : changedBy.Trim(),
            ChangedAtUtc = nowUtc,
        });

        customer.AssignedEmployeeId = newEmployeeId;
        customer.AssignedAgentId = newAgentId;
    }

    /// <summary>Which Employee/Agent applied to a customer on a given date — the transaction-date
    /// attribution rule from Section 4 of the Commission Workflow spec, so a later reassignment never
    /// retroactively changes an already-posted transaction's commission split. Falls back to the
    /// customer's current assignment when there's no history yet (predates this feature) — same
    /// fallback shape as <see cref="ReportsService.ResolveSalespersonAsOf"/>.</summary>
    public static (int? EmployeeId, int? AgentId) ResolveAssignmentAsOf(
        IEnumerable<CustomerAssignmentHistory> history, Customer customer, DateOnly asOf)
    {
        var row = history
            .Where(h => h.EffectiveFrom <= asOf && (h.EffectiveTo == null || h.EffectiveTo >= asOf))
            .OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.Id)
            .FirstOrDefault();
        return row is not null ? (row.EmployeeId, row.AgentId) : (customer.AssignedEmployeeId, customer.AssignedAgentId);
    }

    private static void ValidateHeader(NewCustomerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Customer name is required.");
        if (input.PaymentTermsDays < 0) throw new PostingException("Payment terms cannot be negative.");
        if (input.CreditLimit < 0) throw new PostingException("Credit limit cannot be negative.");
        if (input.OpeningBalance < 0) throw new PostingException("Opening balance cannot be negative.");
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void ApplyScalarFields(Customer c, NewCustomerInput input)
    {
        c.CustomerType = input.CustomerType;
        c.Salutation = Trim(input.Salutation);
        c.FirstName = Trim(input.FirstName);
        c.LastName = Trim(input.LastName);
        c.CompanyName = Trim(input.CompanyName);
        c.Name = input.Name.Trim();
        c.DisplayNameArabic = Trim(input.DisplayNameArabic);
        c.CustomerLanguage = string.IsNullOrWhiteSpace(input.CustomerLanguage) ? "English" : input.CustomerLanguage.Trim();
        c.Trn = Trim(input.Trn);
        c.Email = Trim(input.Email);
        c.Phone = Trim(input.Phone);
        c.WorkPhone = Trim(input.WorkPhone);
        c.Mobile = Trim(input.Mobile);
        c.Address = Trim(input.Address);
        c.Group = Trim(input.Group);
        c.Currency = string.IsNullOrWhiteSpace(input.Currency) ? "AED" : input.Currency.Trim();
        c.CreditLimit = input.CreditLimit;
        c.PaymentTermsDays = input.PaymentTermsDays;
        c.Salesperson = Trim(input.Salesperson);

        c.TaxTreatment = input.TaxTreatment;
        c.PlaceOfSupply = Trim(input.PlaceOfSupply);
        c.OpeningBalance = input.OpeningBalance;
        c.EnablePortal = input.EnablePortal;
        c.Remarks = Trim(input.Remarks);

        var billing = input.Billing;
        c.BillingAttention = Trim(billing?.Attention);
        c.BillingCountry = Trim(billing?.Country);
        c.BillingAddressLine1 = Trim(billing?.AddressLine1);
        c.BillingAddressLine1Arabic = Trim(billing?.AddressLine1Arabic);
        c.BillingAddressLine2 = Trim(billing?.AddressLine2);
        c.BillingAddressLine2Arabic = Trim(billing?.AddressLine2Arabic);
        c.BillingCity = Trim(billing?.City);
        c.BillingEmirate = Trim(billing?.Emirate);
        c.BillingZip = Trim(billing?.Zip);
        c.BillingPhone = Trim(billing?.Phone);
        c.BillingFax = Trim(billing?.Fax);

        var shipping = input.Shipping;
        c.ShippingAttention = Trim(shipping?.Attention);
        c.ShippingCountry = Trim(shipping?.Country);
        c.ShippingAddressLine1 = Trim(shipping?.AddressLine1);
        c.ShippingAddressLine1Arabic = Trim(shipping?.AddressLine1Arabic);
        c.ShippingAddressLine2 = Trim(shipping?.AddressLine2);
        c.ShippingAddressLine2Arabic = Trim(shipping?.AddressLine2Arabic);
        c.ShippingCity = Trim(shipping?.City);
        c.ShippingEmirate = Trim(shipping?.Emirate);
        c.ShippingZip = Trim(shipping?.Zip);
        c.ShippingPhone = Trim(shipping?.Phone);
        c.ShippingFax = Trim(shipping?.Fax);
    }

    private static List<CustomerContactPerson> BuildContactPersons(IEnumerable<ContactPersonInput> inputs)
    {
        var list = new List<CustomerContactPerson>();
        var primaryCount = 0;
        foreach (var i in inputs)
        {
            if (string.IsNullOrWhiteSpace(i.FirstName))
                throw new PostingException("Each contact person needs a first name.");
            if (i.IsPrimary) primaryCount++;
            if (primaryCount > 1)
                throw new PostingException("Only one contact person can be marked as primary.");

            list.Add(new CustomerContactPerson
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

    private static List<CustomerOrganization> BuildOrganizations(IEnumerable<OrganizationInput> inputs)
    {
        var list = new List<CustomerOrganization>();
        foreach (var i in inputs)
        {
            if (string.IsNullOrWhiteSpace(i.Name)) continue; // a stray blank row from the UI — ignore, not an error
            list.Add(new CustomerOrganization { Name = i.Name.Trim(), Trn = Trim(i.Trn) });
        }
        return list;
    }

    /// <summary>Every active custom field defined for the Customer module must have a value if
    /// marked required, and a Dropdown field's value must be one of its own options.</summary>
    private static async Task ValidateAndBuildCustomFieldValuesAsync(
        AegisDbContext db, IEnumerable<CustomFieldValueInput> inputs, List<CustomerCustomFieldValue> target)
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
            target.Add(new CustomerCustomFieldValue { CustomFieldDefinitionId = defId, Value = value });
        }
    }

    /// <summary>At most one tag per tag group.</summary>
    private static async Task ValidateAndBuildTagsAsync(AegisDbContext db, IEnumerable<int> tagIds, List<CustomerTag> target)
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
            target.Add(new CustomerTag { TagId = t.Id });
        }
    }

    // ── Documents ──

    public async Task<List<CustomerDocumentInfo>> GetDocumentsAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.Customers.FindAsync(customerId) ?? throw new PostingException("Customer not found.");

        return await db.CustomerDocuments.AsNoTracking()
            .Where(d => d.CustomerId == customerId)
            .OrderBy(d => d.Id)
            .Select(d => new CustomerDocumentInfo(d.Id, d.FileName, d.ContentType, d.Data.Length, d.UploadedAtUtc))
            .ToListAsync();
    }

    public async Task<CustomerDocument> AddDocumentAsync(int customerId, string fileName, string contentType, byte[] data, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (data.Length > MaxDocumentBytes)
            throw new PostingException($"'{fileName}' is too large — the limit is {MaxDocumentBytes / (1024 * 1024)} MB per file.");

        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.Customers.FindAsync(customerId) ?? throw new PostingException("Customer not found.");

        var count = await db.CustomerDocuments.CountAsync(d => d.CustomerId == customerId);
        if (count >= MaxDocumentsPerCustomer)
            throw new PostingException($"A customer can have at most {MaxDocumentsPerCustomer} documents attached.");

        var doc = new CustomerDocument
        {
            CustomerId = customerId,
            FileName = fileName,
            ContentType = contentType,
            Data = data,
            UploadedAtUtc = nowUtc,
        };
        db.CustomerDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    public async Task RemoveDocumentAsync(int customerId, int documentId)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        _ = await db.Customers.FindAsync(customerId) ?? throw new PostingException("Customer not found.");

        var doc = await db.CustomerDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.CustomerId == customerId)
            ?? throw new PostingException("Document not found.");
        db.CustomerDocuments.Remove(doc);
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetDocumentAsync(int customerId, int documentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Customers.FindAsync(customerId) is null) return null;

        var doc = await db.CustomerDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CustomerId == customerId);
        return doc is null ? null : (doc.FileName, doc.ContentType, doc.Data);
    }

    /// <summary>The next customer code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Customers.Select(c => c.Code).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("C-") && int.TryParse(code.AsSpan(2), out var n) && n > max)
                max = n;
        return $"C-{max + 1:0000}";
    }

    /// <summary>All customers with invoiced / received / outstanding totals from posted documents.</summary>
    public async Task<List<CustomerSummary>> GetSummariesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
        var invoices = await PostedInvoicesAsync(db);
        var receipts = await PostedReceiptsAsync(db);
        var credits = await PostedCreditNotesAsync(db);

        return customers.Select(c =>
        {
            var invoiced = invoices.Where(i => i.CustomerId == c.Id).Sum(i => i.TotalGross);
            var received = receipts.Where(r => r.CustomerId == c.Id).Sum(r => r.Amount);
            var credited = credits.Where(n => n.CustomerId == c.Id).Sum(n => n.TotalGross);
            return new CustomerSummary(c.Id, c.Code, c.Name, c.Trn, c.PaymentTermsDays,
                invoiced, received, invoiced - received - credited, c.Salesperson);
        }).ToList();
    }

    /// <summary>
    /// The four figures shown on the customer detail header: available credit-note balance,
    /// net outstanding receivables (same formula as <see cref="GetSummariesAsync"/>), unallocated
    /// ("on account") receipts, and the customer's own credit limit.
    /// </summary>
    public async Task<CustomerAccountSummary> GetAccountSummaryAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId)
            ?? throw new PostingException("Customer not found.");

        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.CustomerId == customerId).ToList();
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.CustomerId == customerId).ToList();
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.CustomerId == customerId).ToList();
        var (_, receiptIdsWithAllocations) = await AllocationsForAsync(db, receipts.Select(r => r.Id).ToList());

        var invoiced = invoices.Sum(i => i.TotalGross);
        var received = receipts.Sum(r => r.Amount);
        var creditedTotal = credits.Sum(n => n.TotalGross);
        var outstandingReceivables = Math.Max(0, invoiced - received - creditedTotal);

        // A receipt is either 100% on-account or 100% applied — see the identical reasoning in GetAgingAsync.
        var advancePayment = receipts.Where(r => !receiptIdsWithAllocations.Contains(r.Id)).Sum(r => r.Amount);
        var unusedCredits = credits
            .Where(n => n.SettlementMethod == CreditNoteSettlementMethod.CreditOnAccount)
            .Sum(n => n.TotalGross - n.Allocations.Sum(a => a.Amount));

        return new CustomerAccountSummary(unusedCredits, outstandingReceivables, advancePayment, customer.CreditLimit);
    }

    /// <summary>Chronological invoice/receipt statement with running balance.</summary>
    public async Task<List<StatementRow>> GetStatementAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.CustomerId == customerId);
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.CustomerId == customerId);
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.CustomerId == customerId);

        var rows = invoices
            .Select(i => (i.Date, DocNo: i.InvoiceNo, DocType: "Invoice",
                Narration: i.Narration ?? "", Debit: i.TotalGross, Credit: 0m))
            .Concat(receipts.Select(r => (r.Date, DocNo: r.ReceiptNo, DocType: "Receipt",
                Narration: r.Narration ?? "", Debit: 0m, Credit: r.Amount)))
            .Concat(credits.Select(n => (n.Date, DocNo: n.CreditNoteNo, DocType: "Credit Note",
                Narration: n.Narration ?? n.Reason ?? "", Debit: 0m, Credit: n.TotalGross)))
            .OrderBy(r => r.Date).ThenBy(r => r.DocNo)
            .ToList();

        var result = new List<StatementRow>();
        var running = 0m;
        foreach (var r in rows)
        {
            running += r.Debit - r.Credit;
            result.Add(new StatementRow(r.Date, r.DocNo, r.DocType, r.Narration, r.Debit, r.Credit, running));
        }
        return result;
    }

    /// <summary>
    /// AR aging as of a date. Each open invoice's outstanding (gross − allocated receipts)
    /// is bucketed by days past its due date; on-account receipts appear as unallocated credits.
    /// </summary>
    public async Task<List<AgingRow>> GetAgingAsync(DateOnly asOf)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.Date <= asOf).ToList();
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.Date <= asOf).ToList();
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.Date <= asOf).ToList();
        var (allocatedByInvoice, receiptIdsWithAllocations) = await AllocationsForAsync(db, receipts.Select(r => r.Id).ToList());
        var appliedCreditByInvoice = AppliedCreditByInvoice(credits);

        var rows = new List<AgingRow>();
        foreach (var c in customers)
        {
            decimal current = 0, b30 = 0, b60 = 0, b90 = 0, over = 0;
            foreach (var inv in invoices.Where(i => i.CustomerId == c.Id))
            {
                var allocated = allocatedByInvoice.GetValueOrDefault(inv.Id)
                                + credits.Where(n => n.SalesInvoiceId == inv.Id).Sum(n => n.TotalGross)
                                + appliedCreditByInvoice.GetValueOrDefault(inv.Id);
                var outstanding = inv.TotalGross - allocated;
                if (outstanding <= 0) continue;

                var daysPastDue = asOf.DayNumber - inv.DueDate.DayNumber;
                if (daysPastDue <= 0) current += outstanding;
                else if (daysPastDue <= 30) b30 += outstanding;
                else if (daysPastDue <= 60) b60 += outstanding;
                else if (daysPastDue <= 90) b90 += outstanding;
                else over += outstanding;
            }

            // A receipt is either 100% on-account (no allocations at all) or 100% applied (its
            // allocations sum to exactly its Amount — no partial/overpayment state is allowed),
            // so "unallocated" is simply "has zero allocation rows", not merely SalesInvoiceId == null
            // (a receipt spanning multiple invoices also has SalesInvoiceId == null but is fully applied).
            // A credit note's remaining available balance is its own TotalGross minus whatever's
            // already been applied to an invoice (directly or via CreditNoteService.ApplyToInvoiceAsync).
            var unallocated = receipts
                .Where(r => r.CustomerId == c.Id && !receiptIdsWithAllocations.Contains(r.Id))
                .Sum(r => r.Amount)
                + credits.Where(n => n.CustomerId == c.Id && n.SettlementMethod == CreditNoteSettlementMethod.CreditOnAccount)
                    .Sum(n => n.TotalGross - n.Allocations.Sum(a => a.Amount));

            if (current + b30 + b60 + b90 + over > 0 || unallocated > 0)
                rows.Add(new AgingRow(c.Code, c.Name, current, b30, b60, b90, over, unallocated));
        }
        return rows;
    }

    /// <summary>Posted invoices for a customer that still have money owing.</summary>
    public async Task<List<OpenInvoice>> GetOpenInvoicesAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.CustomerId == customerId).ToList();
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.CustomerId == customerId).ToList();
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.CustomerId == customerId).ToList();
        var (allocatedByInvoice, _) = await AllocationsForAsync(db, receipts.Select(r => r.Id).ToList());
        var appliedCreditByInvoice = AppliedCreditByInvoice(credits);

        return invoices
            .Select(i =>
            {
                var allocated = allocatedByInvoice.GetValueOrDefault(i.Id)
                                + credits.Where(n => n.SalesInvoiceId == i.Id).Sum(n => n.TotalGross)
                                + appliedCreditByInvoice.GetValueOrDefault(i.Id);
                var billableLineCount = i.Lines.Count(l => l.Net > 0);
                return new OpenInvoice(i.Id, i.InvoiceNo, i.Date, i.DueDate, i.TotalGross, i.TotalGross - allocated, billableLineCount);
            })
            .Where(o => o.Outstanding > 0)
            .OrderBy(o => o.Date)
            .ToList();
    }

    /// <summary>
    /// Every open (posted, still-owing) invoice across all customers as of a date, with the Employee/
    /// Agent that were assigned to each invoice's customer on the invoice's own date (Section 4's
    /// transaction-date attribution rule) — the Commission Workflow spec's Outstanding Report: same
    /// Customer/Invoice/Amount Due/Days Overdue shape as <see cref="GetOpenInvoicesAsync"/>, but firm-
    /// wide and with the two new columns, each independently filterable by the caller.
    /// </summary>
    public async Task<List<OutstandingInvoiceRow>> GetOutstandingInvoicesAsync(DateOnly asOf)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().Include(c => c.AssignmentHistory).ToListAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.Date <= asOf).ToList();
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.Date <= asOf).ToList();
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.Date <= asOf).ToList();
        var (allocatedByInvoice, _) = await AllocationsForAsync(db, receipts.Select(r => r.Id).ToList());
        var appliedCreditByInvoice = AppliedCreditByInvoice(credits);

        var employeeNames = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.FullName);
        var agentNames = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        var customerById = customers.ToDictionary(c => c.Id);

        var rows = new List<OutstandingInvoiceRow>();
        foreach (var inv in invoices)
        {
            if (!customerById.TryGetValue(inv.CustomerId, out var customer)) continue;

            var allocated = allocatedByInvoice.GetValueOrDefault(inv.Id)
                            + credits.Where(n => n.SalesInvoiceId == inv.Id).Sum(n => n.TotalGross)
                            + appliedCreditByInvoice.GetValueOrDefault(inv.Id);
            var amountDue = inv.TotalGross - allocated;
            if (amountDue <= 0) continue;

            var (employeeId, agentId) = ResolveAssignmentAsOf(customer.AssignmentHistory, customer, inv.Date);
            var daysOverdue = Math.Max(0, asOf.DayNumber - inv.DueDate.DayNumber);

            rows.Add(new OutstandingInvoiceRow(inv.Id, customer.Code, customer.Name, inv.InvoiceNo, inv.Date, inv.DueDate,
                amountDue, daysOverdue,
                employeeId, employeeId is int e ? employeeNames.GetValueOrDefault(e) : null,
                agentId, agentId is int a ? agentNames.GetValueOrDefault(a) : null));
        }
        return rows.OrderByDescending(r => r.DaysOverdue).ToList();
    }

    private static Task<List<SalesInvoice>> PostedInvoicesAsync(AegisDbContext db) =>
        db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<CustomerReceipt>> PostedReceiptsAsync(AegisDbContext db) =>
        db.CustomerReceipts.AsNoTracking()
            .Where(r => r.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<CreditNote>> PostedCreditNotesAsync(AegisDbContext db) =>
        db.CreditNotes.AsNoTracking().Include(n => n.Lines).Include(n => n.Allocations)
            .Where(n => n.Status == VoucherStatus.Posted).ToListAsync();

    /// <summary>How much of each already-loaded (Posted) credit note's balance has been applied to
    /// which invoice — direct (<see cref="CreditNote.SalesInvoiceId"/>) plus on-account notes since
    /// applied via <see cref="CreditNoteService.ApplyToInvoiceAsync"/>.</summary>
    private static Dictionary<int, decimal> AppliedCreditByInvoice(List<CreditNote> credits) =>
        credits.SelectMany(n => n.Allocations)
            .GroupBy(a => a.SalesInvoiceId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

    /// <summary>
    /// For a set of (already Posted-filtered) receipt ids: how much is allocated to each invoice
    /// they touch, and which of those receipt ids have any allocation at all (vs. purely on account).
    /// A receipt's <see cref="CustomerReceipt.SalesInvoiceId"/> is never used here — it can't
    /// represent a receipt spanning multiple invoices, so <see cref="ReceiptAllocation"/> (joined
    /// through the line it targets) is the only correct source of truth.
    /// </summary>
    private static async Task<(Dictionary<int, decimal> AllocatedByInvoice, HashSet<int> ReceiptIdsWithAllocations)>
        AllocationsForAsync(AegisDbContext db, List<int> receiptIds)
    {
        var allocations = await db.ReceiptAllocations.AsNoTracking()
            .Where(a => receiptIds.Contains(a.CustomerReceiptId))
            .Select(a => new { a.CustomerReceiptId, InvoiceId = a.SalesInvoiceLine.SalesInvoiceId, a.Amount })
            .ToListAsync();

        var byInvoice = allocations.GroupBy(a => a.InvoiceId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var receiptsWithAllocations = allocations.Select(a => a.CustomerReceiptId).ToHashSet();
        return (byInvoice, receiptsWithAllocations);
    }
}
