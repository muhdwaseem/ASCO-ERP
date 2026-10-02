namespace AegisErp.Domain;

/// <summary>Spells out an AED amount for the "Amount Chargeable (in words)" / "VAT Amount (in
/// words)" lines UAE tax invoices conventionally carry — e.g. 113267.70 becomes "Dirham One Hundred
/// Thirteen Thousand Two Hundred Sixty Seven and Seventy Only".</summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    {
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };
    private static readonly string[] Tens =
        { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    public static string ToWords(decimal amount)
    {
        amount = Math.Abs(amount);
        var whole = (long)Math.Floor(amount);
        var fraction = (int)Math.Round((amount - whole) * 100, MidpointRounding.AwayFromZero);
        if (fraction >= 100) { whole++; fraction = 0; }

        var wholeWords = ConvertInteger(whole);
        return fraction > 0
            ? $"Dirham {wholeWords} and {ConvertInteger(fraction)} Only"
            : $"Dirham {wholeWords} Only";
    }

    private static string ConvertInteger(long n)
    {
        if (n == 0) return "Zero";

        var parts = new List<string>();
        void Group(long value, string label)
        {
            if (value <= 0) return;
            parts.Add(label.Length > 0 ? $"{ConvertBelowThousand(value)} {label}" : ConvertBelowThousand(value));
        }

        Group(n / 1_000_000_000, "Billion"); n %= 1_000_000_000;
        Group(n / 1_000_000, "Million"); n %= 1_000_000;
        Group(n / 1_000, "Thousand"); n %= 1_000;
        Group(n, "");

        return string.Join(" ", parts);
    }

    private static string ConvertBelowThousand(long n)
    {
        var words = new List<string>();
        if (n >= 100)
        {
            words.Add(Ones[n / 100]);
            words.Add("Hundred");
            n %= 100;
        }
        if (n >= 20)
        {
            words.Add(Tens[n / 10]);
            if (n % 10 > 0) words.Add(Ones[n % 10]);
        }
        else if (n > 0)
        {
            words.Add(Ones[n]);
        }
        return string.Join(" ", words);
    }
}
