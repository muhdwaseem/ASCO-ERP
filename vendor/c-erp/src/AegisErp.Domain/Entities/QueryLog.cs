namespace AegisErp.Domain.Entities;

/// <summary>
/// One call to the natural-language "Ask AI" query feature (<see cref="AegisErp.Infrastructure.Services.AiQueryService"/>) —
/// the question asked, which of the fixed intents Gemini classified it as (or null if unrecognized),
/// the extracted parameters, whether it produced an answer, and cost/latency figures. Exists purely
/// as an audit/eval trail; nothing reads it back to influence a later answer. <see cref="CompanyId"/>
/// is set directly (not inherited through another entity) since this is a top-level per-request log,
/// not a child record of something else already scoped.
/// </summary>
public class QueryLog : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>The user's own words, verbatim.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>The fixed intent Gemini classified this as (e.g. "overdue_invoices"), or null when
    /// classification failed outright (bad/empty model response, not merely "unsupported" — an
    /// explicit "unsupported" classification is itself a valid intent value here).</summary>
    public string? Intent { get; set; }

    /// <summary>The typed parameters Gemini extracted, serialized as JSON — kept for audit/eval
    /// transparency in place of "generated SQL" (there is none in this design; see AiQueryService).</summary>
    public string? ParametersJson { get; set; }

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int? RowCount { get; set; }
    public int LatencyMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    public string AskedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
}
