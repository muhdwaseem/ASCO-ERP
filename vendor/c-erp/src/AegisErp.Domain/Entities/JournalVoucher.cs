namespace AegisErp.Domain.Entities;

/// <summary>
/// A double-entry voucher: a header plus a set of debit/credit lines that must balance
/// before it can be posted to the ledger.
/// </summary>
public class JournalVoucher : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company. Each company has its own ledger and numbering sequence.</summary>
    public int CompanyId { get; set; }

    /// <summary>Generated document number, e.g. "JV-2026-0001". Unique within the company.</summary>
    public string VoucherNo { get; set; } = string.Empty;

    public VoucherType Type { get; set; } = VoucherType.Journal;
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public DateOnly Date { get; set; }
    public string? Narration { get; set; }
    public string? Reference { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedAtUtc { get; set; }

    /// <summary>Approval workflow state — independent of <see cref="Status"/>. A voucher can be
    /// submitted for approval while still a Draft; approval does not itself post it.</summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.None;
    public DateTime? SubmittedForApprovalAtUtc { get; set; }
    public string? SubmittedForApprovalBy { get; set; }
    public DateTime? ApprovalDecisionAtUtc { get; set; }
    public string? ApprovalDecisionBy { get; set; }
    /// <summary>Rejection reason, if the last decision was a rejection.</summary>
    public string? ApprovalNote { get; set; }

    public List<JournalLine> Lines { get; set; } = new();

    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
    public decimal Difference => TotalDebit - TotalCredit;
    public bool IsBalanced => Difference == 0m && TotalDebit > 0m;

    /// <summary>
    /// Validates double-entry rules and transitions the voucher to Posted.
    /// Throws <see cref="PostingException"/> if the voucher is not in a postable state.
    /// </summary>
    public void Post(string postedBy, DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Voucher is already posted.");
        if (Status == VoucherStatus.Void)
            throw new PostingException("A void voucher cannot be posted.");
        if (ApprovalStatus == ApprovalStatus.PendingApproval)
            throw new PostingException("Voucher is pending approval — approve or reject it before posting.");
        if (Lines.Count < 2)
            throw new PostingException("A voucher needs at least two lines.");

        foreach (var line in Lines)
        {
            if (line.AccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no account selected.");
            if (line.Debit < 0 || line.Credit < 0)
                throw new PostingException($"Line {line.LineNo} has a negative amount.");
            if (line.Debit > 0 && line.Credit > 0)
                throw new PostingException($"Line {line.LineNo} cannot be both debit and credit.");
            if (line.Debit == 0 && line.Credit == 0)
                throw new PostingException($"Line {line.LineNo} has no amount.");
        }

        if (TotalDebit != TotalCredit)
            throw new PostingException(
                $"Voucher is out of balance by {Math.Abs(Difference):N2} (Dr {TotalDebit:N2} / Cr {TotalCredit:N2}).");

        Status = VoucherStatus.Posted;
        PostedBy = postedBy;
        PostedAtUtc = nowUtc;
    }

    /// <summary>Submits a draft voucher into the approval queue. Posting is blocked while pending.</summary>
    public void SubmitForApproval(string submittedBy, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Draft)
            throw new PostingException("Only draft vouchers can be submitted for approval.");
        if (ApprovalStatus == ApprovalStatus.PendingApproval)
            throw new PostingException("Voucher is already pending approval.");

        ApprovalStatus = ApprovalStatus.PendingApproval;
        SubmittedForApprovalAtUtc = nowUtc;
        SubmittedForApprovalBy = submittedBy;
        ApprovalDecisionAtUtc = null;
        ApprovalDecisionBy = null;
        ApprovalNote = null;
    }

    /// <summary>Approves a pending voucher, clearing the block on posting.</summary>
    public void Approve(string approvedBy, DateTime nowUtc)
    {
        if (ApprovalStatus != ApprovalStatus.PendingApproval)
            throw new PostingException("Voucher is not pending approval.");

        ApprovalStatus = ApprovalStatus.Approved;
        ApprovalDecisionAtUtc = nowUtc;
        ApprovalDecisionBy = approvedBy;
    }

    /// <summary>Rejects a pending voucher. It stays a Draft, editable and re-submittable.</summary>
    public void Reject(string rejectedBy, DateTime nowUtc, string? note)
    {
        if (ApprovalStatus != ApprovalStatus.PendingApproval)
            throw new PostingException("Voucher is not pending approval.");

        ApprovalStatus = ApprovalStatus.Rejected;
        ApprovalDecisionAtUtc = nowUtc;
        ApprovalDecisionBy = rejectedBy;
        ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    /// <summary>Voids a draft voucher. Posted vouchers already hit the ledger — reverse those with another journal entry instead.</summary>
    public void Void(string voidedBy, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Draft)
            throw new PostingException("Only draft vouchers can be voided — a posted voucher already hit the ledger; reverse it with another journal entry instead.");

        Status = VoucherStatus.Void;
        VoidedBy = voidedBy;
        VoidedAtUtc = nowUtc;
    }
}
