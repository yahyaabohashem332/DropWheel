using System.Numerics;
using System.Text.RegularExpressions;

namespace Petal.Core.Text;

public enum PrivateKind
{
    Email,
    Phone,
    Card,
    Iban,
    Address,
    Secret,
}

/// <summary>Private detail found in a line of text: where it starts, how long it is, and what it is.</summary>
public readonly record struct PrivateMatch(int Start, int Length, PrivateKind Kind);

/// <summary>
/// Finds what shouldn't be shared in text read from a picture: e-mail addresses, phone numbers, card
/// numbers (checked with the Luhn sum), IBANs (checked with their own sum), IP addresses, and what follows
/// "password" or "كلمة المرور". Arabic digits count as digits.
/// </summary>
public static partial class PrivateInfo
{
    public static IReadOnlyList<PrivateMatch> Find(string line)
    {
        // Same length, Western digits, so positions stay the same.
        var text = new string(line.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c).ToArray());
        var found = new List<PrivateMatch>();
        void Add(int start, int length, PrivateKind kind)
        {
            if (!found.Any(f => start < f.Start + f.Length && f.Start < start + length))
                found.Add(new PrivateMatch(start, length, kind));
        }
        foreach (Match m in Secret().Matches(text))
            Add(m.Groups["value"].Index, m.Groups["value"].Length, PrivateKind.Secret);
        foreach (Match m in Email().Matches(text))
            Add(m.Index, m.Length, PrivateKind.Email);
        foreach (Match m in Iban().Matches(text))
            if (IbanValid(m.Value))
                Add(m.Index, m.Length, PrivateKind.Iban);
        foreach (Match m in DigitRun().Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && Luhn(digits))
                Add(m.Index, m.Length, PrivateKind.Card);
            else if (digits.Length is >= 8 and <= 15 && LooksLikePhone(m.Value))
                Add(m.Index, m.Length, PrivateKind.Phone);
        }
        foreach (Match m in Ip().Matches(text))
            if (m.Value.Split('.').All(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) <= 255))
                Add(m.Index, m.Length, PrivateKind.Address);
        return found.OrderBy(f => f.Start).ToList();
    }

    /// <summary>A phone number, not a date, a sum or a version: a plus or a leading zero, or digits in groups.</summary>
    private static bool LooksLikePhone(string run)
    {
        var trimmed = run.Trim();
        if (DateLike().IsMatch(trimmed))
            return false;
        if (trimmed.Contains('.') || trimmed.Contains(','))
            return false;
        return trimmed.StartsWith('+') || trimmed.StartsWith("00", StringComparison.Ordinal) || trimmed.StartsWith('0') || trimmed.StartsWith('(')
            || trimmed.Count(char.IsDigit) >= 9;
    }

    public static bool Luhn(string digits)
    {
        int sum = 0;
        bool twice = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (twice)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }
            sum += d;
            twice = !twice;
        }
        return sum % 10 == 0;
    }

    public static bool IbanValid(string iban)
    {
        var s = new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length < 15 || s.Length > 34)
            return false;
        var moved = s[4..] + s[..4];
        var number = string.Concat(moved.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) : c.ToString()));
        return BigInteger.Parse(number, System.Globalization.CultureInfo.InvariantCulture) % 97 == 1;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<![\w.])(?:\+|00)?\(?\d[\d \-()]{6,22}\d(?![\w.])")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b")]
    private static partial Regex Ip();

    [GeneratedRegex(@"^\d{1,4}[-/ ]\d{1,2}[-/ ]\d{1,4}$")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"(?:password|passcode|pwd|pin|otp|token|api key|secret|كلمة المرور|كلمة السر|رمز التحقق|الرمز السري)\s*[:=：]\s*(?<value>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();
}
