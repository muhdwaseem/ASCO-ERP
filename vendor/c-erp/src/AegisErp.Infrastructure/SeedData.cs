using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure;

/// <summary>
/// Seeds a demo company (Aegis FZE) matching the HTML prototype: roles and users, a chart
/// of accounts, cost centres, fiscal periods, an opening-balance voucher, customers, and
/// AR documents (invoices + receipts) whose GL vouchers are generated through the same
/// domain posting rules the app uses — so the books are balanced by construction.
/// </summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(
        AegisDbContext db, UserManager<AppUser> userManager, RoleManager<IdentityRole> roleManager,
        bool seedDemoData = true, (string Email, string Password, string DisplayName)? bootstrapAdmin = null)
    {
        // Schema creation/upgrade is the caller's job (EnsureCreated for Sqlite dev, MigrateAsync
        // for Postgres/production — see Program.cs) so this can run against either provider.
        await SeedRolesAsync(roleManager);
        if (!seedDemoData)
        {
            // A real client's database: no demo users/company/sample data. Still needs at least
            // one FirmAdmin so someone can sign in and create the client's real company/users
            // from /companies and /users — supplied via config (Seed:AdminEmail/AdminPassword),
            // not hardcoded, since this account has full access to every company.
            if (bootstrapAdmin is { } b)
            {
                try
                {
                    await EnsureUserAsync(userManager, b.Email, b.Password, b.DisplayName,
                        AppRoles.FirmAdmin, AppRoles.Admin, AppRoles.Accountant);
                }
                catch (InvalidOperationException ex)
                {
                    // A Seed:AdminPassword that fails Identity's complexity rules shouldn't take
                    // the whole service down (it previously did — an unhandled exception here
                    // crashed startup entirely). Log it clearly and let the app boot anyway; fix
                    // the env var and redeploy to retry.
                    Console.Error.WriteLine($"[SeedData] Could not seed the bootstrap admin: {ex.Message}");
                }
            }
            // Belt and suspenders: if this database was ever seeded with Seed:DemoData=true
            // before (or on) this deployment, its hardcoded demo accounts — including a
            // FirmAdmin with full cross-company access — would otherwise sit in the database
            // forever with a publicly-known password. Remove them the moment a real deployment
            // switches this flag off, so flipping the config env var alone (no manual DB/SQL
            // access) is enough to close the hole.
            await PurgeDemoAccountsAsync(userManager);
            return;
        }

        await SeedDemoUsersAsync(userManager);

        if (await db.Accounts.AnyAsync()) return; // business data already seeded

        var now = DateTime.UtcNow;

        // ── Companies (tenants) ──
        // Aegis FZE carries the full demo; Meridian is a second client so the company switcher
        // and cross-company isolation are visible from first run.
        var aegis = CompanyDefaults.Aegis();
        var meridian = CompanyDefaults.Meridian();
        db.CompanySetups.AddRange(aegis, meridian);
        await db.SaveChangesAsync();

        await SeedUserAccessAsync(db, userManager, aegis, meridian);
        await SeedAegisAsync(db, aegis.Id, now);
        await SeedMeridianAsync(db, meridian.Id, now);
    }

    /// <summary>Seeds the full Aegis FZE demo: chart of accounts, periods, AR and AP documents.</summary>
    private static async Task SeedAegisAsync(AegisDbContext db, int companyId, DateTime now)
    {
        // Scope the context so every row saved below is stamped with this company automatically.
        db.CurrentCompanyId = companyId;

        // ── Fiscal periods (fiscal year runs Jul–Jun; May 2026 is period 11) ──
        var apr = Period(companyId, "Apr 2026", 10, new(2026, 4, 1), new(2026, 4, 30));
        var may = Period(companyId, "May 2026", 11, new(2026, 5, 1), new(2026, 5, 31));
        var jun = Period(companyId, "Jun 2026", 12, new(2026, 6, 1), new(2026, 6, 30));
        var jul = Period(companyId, "Jul 2026", 1, new(2026, 7, 1), new(2026, 7, 31));
        db.FiscalPeriods.AddRange(apr, may, jun, jul);

        // ── Cost centres (segments) ──
        var ccAdmin = new CostCenter { CompanyId = companyId, Code = "ADM", Name = "Admin" };
        var ccPass = new CostCenter { CompanyId = companyId, Code = "PASS", Name = "Passport Services" };
        var ccTravel = new CostCenter { CompanyId = companyId, Code = "TRV", Name = "Travel & Tourism" };
        var ccHr = new CostCenter { CompanyId = companyId, Code = "HR", Name = "HR" };
        db.CostCenters.AddRange(ccAdmin, ccPass, ccTravel, ccHr);

        // ── Chart of accounts (hierarchical: header groups + postable children) ──
        Account Hdr(string code, string name, AccountType type, Account? parent = null) =>
            new() { CompanyId = companyId, Code = code, Name = name, Type = type, IsPostable = false, Parent = parent };
        Account A(string code, string name, AccountType type, Account parent, string category) =>
            new() { CompanyId = companyId, Code = code, Name = name, Type = type, IsPostable = true, Parent = parent, Category = category };

        var hAssets = Hdr("10000", "Assets", AccountType.Asset);
        var hCurrent = Hdr("11000", "Current Assets", AccountType.Asset, hAssets);
        var hFixed = Hdr("15000", "Fixed Assets", AccountType.Asset, hAssets);
        var hLiab = Hdr("20000", "Liabilities", AccountType.Liability);
        var hEquity = Hdr("30000", "Equity", AccountType.Equity);
        var hRevenue = Hdr("40000", "Revenue", AccountType.Income);
        var hExpense = Hdr("50000", "Expenses", AccountType.Expense);

        var accounts = new List<Account>
        {
            hAssets, hCurrent, hFixed, hLiab, hEquity, hRevenue, hExpense,
            A("11020", "ENBD Current Account", AccountType.Asset, hCurrent, "Cash and cash equivalents"),
            A("11030", "Mashreq Bank Account", AccountType.Asset, hCurrent, "Cash and cash equivalents"),
            A(WellKnownAccounts.AccountsReceivable, "Accounts Receivable", AccountType.Asset, hCurrent, "Accounts receivable"),
            A("13010", "Prepaid Expenses & VAT Input", AccountType.Asset, hCurrent, "Current asset"),
            A("15010", "Property & Equipment", AccountType.Asset, hFixed, "Fixed asset"),
            A("21010", "Accounts Payable", AccountType.Liability, hLiab, "Accounts payable"),
            A(WellKnownAccounts.VatPayable, "VAT Payable", AccountType.Liability, hLiab, "Current liability"),
            A(WellKnownAccounts.DeferredRevenue, "Deferred Revenue", AccountType.Liability, hLiab, "Current liability"),
            A("31010", "Share Capital & Retained Earnings", AccountType.Equity, hEquity, "Equity"),
            A("41010", "Passport Services Revenue", AccountType.Income, hRevenue, "Income"),
            A("41020", "Travel & Tourism Revenue", AccountType.Income, hRevenue, "Income"),
            A("51010", "Salaries & Wages", AccountType.Expense, hExpense, "Direct expense"),
            A("51020", "Office Rent", AccountType.Expense, hExpense, "Indirect expense"),
            A("51030", "Utilities & Communications", AccountType.Expense, hExpense, "Indirect expense"),
            A("51040", "IT & Software", AccountType.Expense, hExpense, "Indirect expense"),
            A("51050", "Office Supplies", AccountType.Expense, hExpense, "Indirect expense"),
        };
        db.Accounts.AddRange(accounts);

        // ── Customers ──
        var emirates = new Customer { CompanyId = companyId, Code = "C-0001", Name = "Emirates Group", Group = "Corporate", CreditLimit = 500_000, Trn = "100234567890003", Email = "ap@emirates.example", Phone = "+971 4 708 1111", PaymentTermsDays = 30 };
        var spiceJet = new Customer { CompanyId = companyId, Code = "C-0002", Name = "Spice Jet Ltd", Group = "Corporate", CreditLimit = 300_000, Trn = "100345678900003", Email = "finance@spicejet.example", Phone = "+971 4 555 2222", PaymentTermsDays = 30 };
        var dnata = new Customer { CompanyId = companyId, Code = "C-0003", Name = "DNATA Travel", Group = "Corporate", CreditLimit = 250_000, Trn = "100456789010003", Email = "accounts@dnata.example", Phone = "+971 4 316 6666", PaymentTermsDays = 45 };
        db.Customers.AddRange(emirates, spiceJet, dnata);

        // ── Vendors ──
        var etisalat = new Vendor { CompanyId = companyId, Code = "V-0001", Name = "Emirates Telecom (Etisalat)", Group = "Utility", Trn = "100567890120003", Email = "billing@etisalat.example", Phone = "+971 800 101", PaymentTermsDays = 30 };
        var alFuttaim = new Vendor { CompanyId = companyId, Code = "V-0002", Name = "Al Futtaim Office Supplies", Group = "Supplier", Trn = "100678901230003", Email = "ar@alfuttaim.example", Phone = "+971 4 206 8888", PaymentTermsDays = 45 };
        var gulfIt = new Vendor { CompanyId = companyId, Code = "V-0003", Name = "Gulf IT Services", Group = "Contractor", Trn = "100789012340003", Email = "accounts@gulfit.example", Phone = "+971 4 333 7777", PaymentTermsDays = 30 };
        db.Vendors.AddRange(etisalat, alFuttaim, gulfIt);

        await db.SaveChangesAsync();

        var byCode = accounts.ToDictionary(a => a.Code);
        int Acc(string code) => byCode[code].Id;

        // ── Opening balances (balanced voucher dated 30 Apr) ──
        AddVoucher(db, VoucherType.Opening, "OB-2026-0001", new(2026, 4, 30), apr.Id,
            "Opening balances — carried forward", now, new[]
            {
                Line(Acc("11020"), ccAdmin.Id, "ENBD opening", 1_130_000, 0),
                Line(Acc("11030"), ccAdmin.Id, "Mashreq opening", 820_000, 0),
                Line(Acc("12010"), ccAdmin.Id, "AR opening (on account)", 1_650_000, 0),
                Line(Acc("13010"), ccAdmin.Id, "Prepaid/VAT input opening", 412_040, 0),
                Line(Acc("15010"), ccAdmin.Id, "Fixed assets opening", 2_100_000, 0),
                Line(Acc("21010"), ccAdmin.Id, "AP opening", 0, 866_333),
                Line(Acc("22010"), ccAdmin.Id, "VAT payable opening", 0, 42_000),
                Line(Acc("31010"), ccAdmin.Id, "Equity balancing figure", 0, 5_203_707),
            });

        // ── May 2026 expenses (rent + payroll) ──
        AddVoucher(db, VoucherType.Payment, "PV-2026-0038", new(2026, 5, 5), may.Id,
            "May 2026 office rent — Al Futtaim Properties", now, new[]
            {
                Line(Acc("51020"), ccAdmin.Id, "Office rent", 79_167, 0),
                Line(Acc("11020"), ccAdmin.Id, "Bank payment", 0, 79_167),
            });
        AddVoucher(db, VoucherType.Journal, "JV-2026-0001", new(2026, 5, 25), may.Id,
            "May 2026 payroll run", now, new[]
            {
                Line(Acc("51010"), ccHr.Id, "Salaries & wages", 84_645, 0),
                Line(Acc("11020"), ccHr.Id, "Net pay disbursed", 0, 84_645),
            });

        // ── Sales invoices (each generates its GL voucher) ──
        var inv141 = AddInvoice(db, "INV-2026-0141", spiceJet, new(2026, 5, 3), may.Id, now,
            "Passport processing — Spice Jet Ltd", "Ahmed Al Mansoori", byCode, ccPass.Id,
            ("Passport processing services (100 applications)", "41010", 100, 850, 0.05m));

        var inv142 = AddInvoice(db, "INV-2026-0142", emirates, new(2026, 5, 5), may.Id, now,
            "Corporate travel package — Emirates Group", "Ahmed Al Mansoori", byCode, ccTravel.Id,
            ("Corporate travel package — May", "41020", 1, 90_000, 0.05m));

        var inv143 = AddInvoice(db, "INV-2026-0143", dnata, new(2026, 6, 15), jun.Id, now,
            "Visa processing retainer — DNATA", "Ahmed Al Mansoori", byCode, ccPass.Id,
            ("Visa processing retainer — June", "41010", 1, 40_000, 0.05m));

        AddInvoice(db, "INV-2026-0144", dnata, new(2026, 7, 5), jul.Id, now,
            "Travel desk services — DNATA", "Ahmed Al Mansoori", byCode, ccTravel.Id,
            ("Travel desk services — July", "41020", 1, 30_000, 0.05m));

        // ── Customer receipts ──
        AddReceipt(db, "RV-2026-0028", emirates, inv142, new(2026, 5, 8), may.Id, Acc("11020"),
            94_500, "Receipt — Emirates Group settles INV-2026-0142", now, byCode);
        AddReceipt(db, "RV-2026-0029", dnata, inv143, new(2026, 6, 28), jun.Id, Acc("11020"),
            20_000, "Part payment — DNATA against INV-2026-0143", now, byCode);

        // ── Purchase invoices (each generates its GL voucher: Dr expense / Dr VAT input / Cr AP) ──
        var pinv1 = AddPurchaseInvoice(db, "PINV-2026-0001", etisalat, "ETL-559120", new(2026, 5, 6), may.Id, now,
            "May 2026 telecom & internet — Etisalat", "Fatima Al Rashidi", byCode, ccAdmin.Id,
            ("Corporate internet & phone lines — May", "51030", 1, 6_800, 0.05m));

        var pinv2 = AddPurchaseInvoice(db, "PINV-2026-0002", gulfIt, "GIT-2026-118", new(2026, 5, 12), may.Id, now,
            "Cloud hosting & IT support — Gulf IT", "Fatima Al Rashidi", byCode, ccAdmin.Id,
            ("Cloud hosting & managed IT support — May", "51040", 1, 18_500, 0.05m));

        AddPurchaseInvoice(db, "PINV-2026-0003", alFuttaim, "AF-INV-77421", new(2026, 6, 3), jun.Id, now,
            "Office supplies & stationery — Al Futtaim", "Fatima Al Rashidi", byCode, ccAdmin.Id,
            ("Office stationery & consumables", "51050", 1, 4_250, 0.05m));

        // ── Vendor payments (Dr AP / Cr bank) ──
        AddVendorPayment(db, "PV-2026-0040", etisalat, pinv1, new(2026, 5, 20), may.Id, Acc("11020"),
            7_140, "Payment — Etisalat settles PINV-2026-0001", now, byCode);
        AddVendorPayment(db, "PV-2026-0041", gulfIt, pinv2, new(2026, 5, 28), may.Id, Acc("11030"),
            10_000, "Part payment — Gulf IT against PINV-2026-0002", now, byCode);

        await db.SaveChangesAsync();
        db.CurrentCompanyId = null;
    }

    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A second, smaller client: its own chart of accounts (reusing the same account codes, which is
    /// legal now that codes are unique per company), one customer, one vendor and a few documents.
    /// </summary>
    private static async Task SeedMeridianAsync(AegisDbContext db, int companyId, DateTime now)
    {
        db.CurrentCompanyId = companyId;

        var q1 = Period(companyId, "Jan 2026", 1, new(2026, 1, 1), new(2026, 1, 31));
        var jul = Period(companyId, "Jul 2026", 7, new(2026, 7, 1), new(2026, 7, 31));
        db.FiscalPeriods.AddRange(q1, jul);

        var ccOps = new CostCenter { CompanyId = companyId, Code = "OPS", Name = "Operations" };
        db.CostCenters.Add(ccOps);

        Account Hdr(string code, string name, AccountType type, Account? parent = null) =>
            new() { CompanyId = companyId, Code = code, Name = name, Type = type, IsPostable = false, Parent = parent };
        Account A(string code, string name, AccountType type, Account parent, string category) =>
            new() { CompanyId = companyId, Code = code, Name = name, Type = type, IsPostable = true, Parent = parent, Category = category };

        var hAssets = Hdr("10000", "Assets", AccountType.Asset);
        var hCurrent = Hdr("11000", "Current Assets", AccountType.Asset, hAssets);
        var hLiab = Hdr("20000", "Liabilities", AccountType.Liability);
        var hEquity = Hdr("30000", "Equity", AccountType.Equity);
        var hRevenue = Hdr("40000", "Revenue", AccountType.Income);
        var hExpense = Hdr("50000", "Expenses", AccountType.Expense);

        var accounts = new List<Account>
        {
            hAssets, hCurrent, hLiab, hEquity, hRevenue, hExpense,
            A("11020", "ADCB Current Account", AccountType.Asset, hCurrent, "Cash and cash equivalents"),
            A(WellKnownAccounts.AccountsReceivable, "Accounts Receivable", AccountType.Asset, hCurrent, "Accounts receivable"),
            A(WellKnownAccounts.VatInput, "Prepaid Expenses & VAT Input", AccountType.Asset, hCurrent, "Current asset"),
            A(WellKnownAccounts.AccountsPayable, "Accounts Payable", AccountType.Liability, hLiab, "Accounts payable"),
            A(WellKnownAccounts.VatPayable, "VAT Payable", AccountType.Liability, hLiab, "Current liability"),
            A(WellKnownAccounts.DeferredRevenue, "Deferred Revenue", AccountType.Liability, hLiab, "Current liability"),
            A("31010", "Share Capital & Retained Earnings", AccountType.Equity, hEquity, "Equity"),
            A("41010", "Trading Revenue", AccountType.Income, hRevenue, "Income"),
            A("51010", "Cost of Goods Sold", AccountType.Expense, hExpense, "Direct expense"),
        };
        db.Accounts.AddRange(accounts);

        var acme = new Customer { CompanyId = companyId, Code = "C-0001", Name = "Acme Industries", Group = "Corporate", CreditLimit = 200_000, Trn = "100112233440003", PaymentTermsDays = 30 };
        db.Customers.Add(acme);

        var supplier = new Vendor { CompanyId = companyId, Code = "V-0001", Name = "Northline Logistics", Group = "Supplier", Trn = "100556677880003", PaymentTermsDays = 30 };
        db.Vendors.Add(supplier);

        await db.SaveChangesAsync();

        var byCode = accounts.ToDictionary(a => a.Code);
        int Acc(string code) => byCode[code].Id;

        AddVoucher(db, VoucherType.Opening, "OB-2026-0001", new(2026, 1, 1), q1.Id,
            "Opening balances — Meridian Trading", now, new[]
            {
                Line(Acc("11020"), ccOps.Id, "ADCB opening", 300_000, 0),
                Line(Acc("31010"), ccOps.Id, "Equity balancing figure", 0, 300_000),
            });

        AddInvoice(db, "INV-2026-0001", acme, new(2026, 7, 8), jul.Id, now,
            "Q3 goods supply — Acme Industries", "System Admin", byCode, ccOps.Id,
            ("Bulk goods supply — July", "41010", 1, 60_000, 0.05m));

        AddPurchaseInvoice(db, "PINV-2026-0001", supplier, "NL-88214", new(2026, 7, 10), jul.Id, now,
            "Freight & handling — Northline", "System Admin", byCode, ccOps.Id,
            ("Freight and handling charges", "51010", 1, 12_000, 0.05m));

        await db.SaveChangesAsync();
        db.CurrentCompanyId = null;
    }

    /// <summary>
    /// Grants company access. This models the auditor setup: the admin spans every client, the
    /// finance user is assigned to Aegis only, and the viewer gets read-only on both.
    /// </summary>
    private static async Task SeedUserAccessAsync(
        AegisDbContext db, UserManager<AppUser> users, CompanySetup aegis, CompanySetup meridian)
    {
        async Task Grant(string email, CompanySetup company, string role)
        {
            var user = await users.FindByNameAsync(email);
            if (user is null) return;
            if (await db.UserCompanyAccess.AnyAsync(a => a.UserId == user.Id && a.CompanyId == company.Id)) return;
            db.UserCompanyAccess.Add(new UserCompanyAccess { UserId = user.Id, CompanyId = company.Id, Role = role });
        }

        await Grant(DemoAdminEmail, aegis, AppRoles.Admin);
        await Grant(DemoAdminEmail, meridian, AppRoles.Admin);
        await Grant(DemoFinanceEmail, aegis, AppRoles.Accountant);   // Aegis only — not Meridian
        await Grant(DemoViewerEmail, aegis, AppRoles.Viewer);
        await Grant(DemoViewerEmail, meridian, AppRoles.Viewer);
        await db.SaveChangesAsync();
    }

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roles)
    {
        foreach (var role in new[] { AppRoles.FirmAdmin, AppRoles.Admin, AppRoles.Accountant, AppRoles.Viewer, AppRoles.Employee })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
    }

    // These credentials are never displayed anywhere in the app (see Login.razor) — they're
    // handed to whoever provisions the demo/first-run login out of band.
    public const string DemoAdminEmail = "owner@aegiserp.com";
    public const string DemoAdminPassword = "Aegis#Owner2026";
    public const string DemoFinanceEmail = "accounts@aegiserp.com";
    public const string DemoFinancePassword = "Aegis#Finance2026";
    public const string DemoViewerEmail = "readonly@aegiserp.com";
    public const string DemoViewerPassword = "Aegis#View2026";

    private static async Task SeedDemoUsersAsync(UserManager<AppUser> users)
    {
        await MigrateOrCreateUserAsync(users, "admin@aegisfze.com", DemoAdminEmail, DemoAdminPassword, "System Admin", AppRoles.FirmAdmin, AppRoles.Admin, AppRoles.Accountant);
        await MigrateOrCreateUserAsync(users, "finance@aegisfze.com", DemoFinanceEmail, DemoFinancePassword, "Fatima Al Rashidi", AppRoles.Accountant);
        await MigrateOrCreateUserAsync(users, "viewer@aegisfze.com", DemoViewerEmail, DemoViewerPassword, "Ahmed Al Mansoori", AppRoles.Viewer);
    }

    /// <summary>
    /// Deletes the three hardcoded demo accounts (and, since they're never renamed once they
    /// migrated once, the original pre-rotation emails too) if they exist — run whenever a
    /// deployment declares itself a real client (Seed:DemoData=false) so a database that was
    /// ever seeded with demo data before doesn't keep a publicly-known-password FirmAdmin
    /// account alive indefinitely. Safe/no-op on a database that never had them.
    /// </summary>
    private static async Task PurgeDemoAccountsAsync(UserManager<AppUser> users)
    {
        foreach (var email in new[]
        {
            DemoAdminEmail, DemoFinanceEmail, DemoViewerEmail,
            "admin@aegisfze.com", "finance@aegisfze.com", "viewer@aegisfze.com",
        })
        {
            var user = await users.FindByNameAsync(email);
            if (user is null) continue;
            var result = await users.DeleteAsync(user);
            if (!result.Succeeded)
                Console.Error.WriteLine($"[SeedData] Could not purge demo account {email}: " +
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    /// <summary>
    /// Rotates a previously-seeded demo account (old email/password) onto new credentials in
    /// place — renaming rather than creating a second user means UserCompanyAccess and every
    /// other row keyed by UserId survives untouched, and the old password stops working.
    /// Databases that never had the old account just get it created fresh under the new email.
    /// </summary>
    private static async Task MigrateOrCreateUserAsync(
        UserManager<AppUser> users, string oldEmail, string newEmail, string newPassword,
        string displayName, params string[] roles)
    {
        if (await users.FindByNameAsync(newEmail) is not null) return; // already migrated/created

        var existing = await users.FindByNameAsync(oldEmail);
        if (existing is not null)
        {
            await users.SetUserNameAsync(existing, newEmail);
            await users.SetEmailAsync(existing, newEmail);
            existing.EmailConfirmed = true;
            await users.UpdateAsync(existing);
            await users.RemovePasswordAsync(existing);
            var resetResult = await users.AddPasswordAsync(existing, newPassword);
            if (!resetResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to rotate credentials for {oldEmail} -> {newEmail}: {string.Join("; ", resetResult.Errors.Select(e => e.Description))}");
            return;
        }

        await EnsureUserAsync(users, newEmail, newPassword, displayName, roles);
    }

    private static async Task EnsureUserAsync(
        UserManager<AppUser> users, string email, string password, string displayName, params string[] roles)
    {
        if (await users.FindByNameAsync(email) is not null) return;
        var user = new AppUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Failed to seed user {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        await users.AddToRolesAsync(user, roles);
    }

    private static FiscalPeriod Period(int companyId, string name, int no, DateOnly start, DateOnly end) =>
        new() { CompanyId = companyId, Name = name, Year = 2026, PeriodNo = no, StartDate = start, EndDate = end };

    private static JournalLine Line(int accountId, int? costCenterId, string desc, decimal dr, decimal cr) =>
        new() { AccountId = accountId, CostCenterId = costCenterId, Description = desc, Debit = dr, Credit = cr };

    private static JournalVoucher AddVoucher(AegisDbContext db, VoucherType type, string no, DateOnly date,
        int periodId, string narration, DateTime now, JournalLine[] lines)
    {
        var v = new JournalVoucher
        {
            VoucherNo = no,
            Type = type,
            Date = date,
            FiscalPeriodId = periodId,
            Narration = narration,
            CreatedBy = "System Admin",
            CreatedAtUtc = now,
        };
        var n = 1;
        foreach (var l in lines) { l.LineNo = n++; v.Lines.Add(l); }
        v.Post("System Admin", now); // enforces balance even for seed data
        db.JournalVouchers.Add(v);
        return v;
    }

    private static SalesInvoice AddInvoice(AegisDbContext db, string no, Customer customer, DateOnly date,
        int periodId, DateTime now, string narration, string createdBy,
        Dictionary<string, Account> byCode, int costCenterId,
        params (string Desc, string RevenueCode, decimal Qty, decimal Price, decimal VatRate)[] lines)
    {
        var invoice = new SalesInvoice
        {
            InvoiceNo = no,
            CustomerId = customer.Id,
            Date = date,
            DueDate = date.AddDays(customer.PaymentTermsDays),
            FiscalPeriodId = periodId,
            Narration = narration,
            CreatedBy = createdBy,
            CreatedAtUtc = now,
        };
        var n = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = n++,
                Description = l.Desc,
                RevenueAccountId = byCode[l.RevenueCode].Id,
                CostCenterId = costCenterId,
                Quantity = l.Qty,
                UnitPrice = l.Price,
                VatRate = l.VatRate,
            });
        invoice.Post(createdBy, now);

        // Same voucher shape the SalesInvoiceService generates.
        var jLines = new List<JournalLine>
        {
            Line(byCode[WellKnownAccounts.AccountsReceivable].Id, null, $"{customer.Name} — {no}", invoice.TotalGross, 0),
        };
        jLines.AddRange(invoice.Lines.Select(l =>
            Line(l.RevenueAccountId, l.CostCenterId, l.Description, 0, l.Net)));
        if (invoice.TotalVat > 0)
            jLines.Add(Line(byCode[WellKnownAccounts.VatPayable].Id, null, $"Output VAT — {no}", 0, invoice.TotalVat));

        invoice.JournalVoucher = AddVoucher(db, VoucherType.SalesInvoice, no, date, periodId, narration, now, jLines.ToArray());
        db.SalesInvoices.Add(invoice);
        return invoice;
    }

    private static PurchaseInvoice AddPurchaseInvoice(AegisDbContext db, string no, Vendor vendor, string? vendorRef,
        DateOnly date, int periodId, DateTime now, string narration, string createdBy,
        Dictionary<string, Account> byCode, int costCenterId,
        params (string Desc, string ExpenseCode, decimal Qty, decimal Price, decimal VatRate)[] lines)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceNo = no,
            VendorRef = vendorRef,
            VendorId = vendor.Id,
            Date = date,
            DueDate = date.AddDays(vendor.PaymentTermsDays),
            FiscalPeriodId = periodId,
            Narration = narration,
            CreatedBy = createdBy,
            CreatedAtUtc = now,
        };
        var n = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new PurchaseInvoiceLine
            {
                LineNo = n++,
                Description = l.Desc,
                ExpenseAccountId = byCode[l.ExpenseCode].Id,
                CostCenterId = costCenterId,
                Quantity = l.Qty,
                UnitPrice = l.Price,
                VatRate = l.VatRate,
            });
        invoice.Post(now);

        // Same voucher shape the PurchaseInvoiceService generates.
        var jLines = new List<JournalLine>();
        jLines.AddRange(invoice.Lines.Select(l => Line(l.ExpenseAccountId, l.CostCenterId, l.Description, l.Net, 0)));
        if (invoice.TotalVat > 0)
            jLines.Add(Line(byCode[WellKnownAccounts.VatInput].Id, null, $"Input VAT — {no}", invoice.TotalVat, 0));
        jLines.Add(Line(byCode[WellKnownAccounts.AccountsPayable].Id, null, $"{vendor.Name} — {no}", 0, invoice.TotalGross));

        invoice.JournalVoucher = AddVoucher(db, VoucherType.PurchaseInvoice, no, date, periodId, narration, now, jLines.ToArray());
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    private static void AddVendorPayment(AegisDbContext db, string no, Vendor vendor, PurchaseInvoice? invoice,
        DateOnly date, int periodId, int bankAccountId, decimal amount, string narration, DateTime now,
        Dictionary<string, Account> byCode)
    {
        var payment = new VendorPayment
        {
            PaymentNo = no,
            VendorId = vendor.Id,
            PurchaseInvoice = invoice,
            Date = date,
            FiscalPeriodId = periodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            Narration = narration,
            CreatedBy = "System Admin",
            CreatedAtUtc = now,
        };
        payment.Post(now);

        payment.JournalVoucher = AddVoucher(db, VoucherType.Payment, no, date, periodId, narration, now, new[]
        {
            Line(byCode[WellKnownAccounts.AccountsPayable].Id, null, $"{vendor.Name} — {no}", amount, 0),
            Line(bankAccountId, null, narration, 0, amount),
        });
        db.VendorPayments.Add(payment);
    }

    private static void AddReceipt(AegisDbContext db, string no, Customer customer, SalesInvoice? invoice,
        DateOnly date, int periodId, int bankAccountId, decimal amount, string narration, DateTime now,
        Dictionary<string, Account> byCode)
    {
        var receipt = new CustomerReceipt
        {
            ReceiptNo = no,
            CustomerId = customer.Id,
            SalesInvoice = invoice,
            Date = date,
            FiscalPeriodId = periodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            Narration = narration,
            CreatedBy = "System Admin",
            CreatedAtUtc = now,
        };
        receipt.Post(now);

        receipt.JournalVoucher = AddVoucher(db, VoucherType.Receipt, no, date, periodId, narration, now, new[]
        {
            Line(bankAccountId, null, narration, amount, 0),
            Line(byCode[WellKnownAccounts.AccountsReceivable].Id, null, $"{customer.Name} — {no}", 0, amount),
        });
        db.CustomerReceipts.Add(receipt);
    }
}
