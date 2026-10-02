using System.Globalization;
using System.Text;
using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record WpsFile(string FileName, string Content);

/// <summary>
/// Builds a UAE WPS (Wage Protection System) salary file — a headerless CSV of one SCR (Salary
/// Control Record) line followed by one EDR (Employee Detail Record) line per employee, per the
/// field layout documented in the plan (sourced from Zoho Payroll's UAE WPS SIF guide). This
/// follows the published structure but has NOT been validated against any specific bank's WPS
/// portal — verify before a real first submission (surfaced in the UI too, not just here).
/// </summary>
public class WpsFileService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;

    public WpsFileService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<WpsFile> GenerateSifAsync(int payrollRunId, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var run = await db.PayrollRuns.Include(r => r.FiscalPeriod).Include(r => r.Lines).ThenInclude(l => l.Employee)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId)
            ?? throw new PostingException("Payroll run not found.");
        if (run.Status != VoucherStatus.Posted)
            throw new PostingException("Only a posted payroll run can be exported to WPS.");
        if (run.Lines.Count == 0)
            throw new PostingException("Payroll run has no employees.");

        var company = (_current.CompanyId is int id
            ? await db.CompanySetups.FirstOrDefaultAsync(c => c.Id == id)
            : await db.CompanySetups.FirstOrDefaultAsync())
            ?? throw new PostingException("No company is selected. Pick a company before exporting a WPS file.");
        if (string.IsNullOrWhiteSpace(company.MohreEstablishmentId))
            throw new PostingException("Set the company's MOHRE Establishment ID (Company Setup → System) before exporting a WPS file.");
        if (string.IsNullOrWhiteSpace(company.WpsBankAgentId))
            throw new PostingException("Set the company's WPS Bank Agent ID (Company Setup → System) before exporting a WPS file.");

        foreach (var line in run.Lines)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(line.Employee.LabourCardNumber)) missing.Add("Labour Card Number");
            if (string.IsNullOrWhiteSpace(line.Employee.WpsAgentId)) missing.Add("WPS Agent ID");
            if (string.IsNullOrWhiteSpace(line.Employee.Iban)) missing.Add("IBAN");
            if (missing.Count > 0)
                throw new PostingException($"{line.Employee.FullName} ({line.Employee.EmployeeCode}) is missing: {string.Join(", ", missing)}.");
        }

        var period = run.FiscalPeriod;
        var sb = new StringBuilder();

        sb.Append("SCR,")
          .Append(company.MohreEstablishmentId).Append(',')
          .Append(company.WpsBankAgentId).Append(',')
          .Append(nowUtc.ToString("yyyy-MM-dd")).Append(',')
          .Append(nowUtc.ToString("HHmm")).Append(',')
          .Append(period.StartDate.ToString("MMyyyy")).Append(',')
          .Append(run.Lines.Count).Append(',')
          .Append(run.TotalNet.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
          .Append("AED")
          .Append('\n');

        foreach (var line in run.Lines)
        {
            var e = line.Employee;
            var fixedIncome = line.BasicSalary + line.HousingAllowance + line.TransportAllowance;
            var variableIncome = line.OtherAllowance;
            var daysInPeriod = period.EndDate.DayNumber - period.StartDate.DayNumber + 1;

            sb.Append("EDR,")
              .Append(e.LabourCardNumber!.PadLeft(14, '0')).Append(',')
              .Append(e.WpsAgentId).Append(',')
              .Append(e.Iban).Append(',')
              .Append(period.StartDate.ToString("yyyy-MM-dd")).Append(',')
              .Append(period.EndDate.ToString("yyyy-MM-dd")).Append(',')
              .Append(daysInPeriod).Append(',')
              .Append(fixedIncome.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(variableIncome.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append("")
              .Append('\n');
        }

        var fileName = $"{company.MohreEstablishmentId}{nowUtc:yyMMdd}{nowUtc:HHmmss}.sif";
        return new WpsFile(fileName, sb.ToString());
    }
}
