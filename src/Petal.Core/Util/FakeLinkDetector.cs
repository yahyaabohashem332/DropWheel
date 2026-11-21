using System.Text.RegularExpressions;

namespace Petal.Core.Util;

/// <summary>
/// Detects suspicious/fake links using local heuristic rules only (offline, privacy-first).
/// Checks for homograph attacks, URL shorteners, suspicious TLDs, brand impersonation, etc.
/// </summary>
public static class FakeLinkDetector
{
    // Known URL shorteners (high risk - hide destination)
    private static readonly HashSet<string> UrlShorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "adf.ly", "shorte.st",
        "cutt.ly", "rebrand.ly", "short.io", "lnkd.in", "fb.me", "trib.al", "ift.tt", "wp.me",
        "v.gd", "tr.im", "url.ie", "qr.ae", "po.st", "bc.vc", "link.tl", "fur.ly", "cli.gs",
        "u.to", "rzl.io", "shorturl.at", "rb.gy", "dub.sh", "kutt.it", "shorturl.com"
    };

    // Suspicious TLDs (often used for phishing, spam, malware)
    private static readonly HashSet<string> SuspiciousTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "tk", "ml", "ga", "cf", "gq", "xyz", "top", "loan", "work", "party", "bid", "review",
        "racing", "stream", "download", "click", "link", "country", "science", "trade", "gdn",
        "men", "mom", "wang", "club", "online", "site", "space", "website", "webcam", "accountant",
        "date", "faith", "cricket", "win", "pw", "cfd", "monster", "autos", "homes", "motorcycles",
        "yachts", "boats", "hair", "beauty", "skin", "makeup", "quest", "zip", "mov", "meme",
        "plumbing", "phd", "prof", "esq", "foo", "bar", "baz", "new", "dev", "app", "page"
    };

    // Known major brands (for impersonation detection)
    private static readonly HashSet<string> MajorBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "google", "microsoft", "apple", "amazon", "facebook", "instagram", "whatsapp", "twitter",
        "x.com", "linkedin", "github", "gitlab", "bitbucket", "paypal", "stripe", "square",
        "shopify", "ebay", "etsy", "walmart", "target", "bestbuy", "homedepot", "lowes",
        "netflix", "spotify", "youtube", "twitch", "discord", "slack", "teams", "zoom",
        "dropbox", "box", "drive", "onedrive", "icloud", "adobe", "salesforce", "oracle",
        "ibm", "intel", "amd", "nvidia", "qualcomm", "cisco", "vmware", "docker", "kubernetes",
        "aws", "azure", "gcp", "cloudflare", "fastly", "akamai", "digitalocean", "linode",
        "bankofamerica", "chase", "wellsfargo", "citibank", "capitalone", "americanexpress",
        "visa", "mastercard", "discover", "paypal", "venmo", "cashapp", "zelle", "wise",
        "binance", "coinbase", "kraken", "crypto", "blockchain", "metamask", "trustwallet"
    };

    // Homograph lookalike characters (Cyrillic, Greek, etc. that look like Latin)
    private static readonly Dictionary<char, char> HomographMap = new()
    {
        // Cyrillic lookalikes
        { 'а', 'a' }, { 'е', 'e' }, { 'о', 'o' }, { 'р', 'p' }, { 'с', 'c' }, { 'у', 'y' },
        { 'х', 'x' }, { 'і', 'i' }, { 'ј', 'j' }, { 'ԁ', 'd' }, { 'ԉ', 'g' }, { 'ӏ', 'l' },
        { 'ѕ', 's' }, { 'ԛ', 'q' }, { 'ԝ', 'w' }, { 'ԟ', 'h' }, { 'ԡ', 'k' }, { 'ԧ', 'r' },
        { 'ԫ', 'u' }, { 'ԭ', 'b' }, { 'ѵ', 'v' }, { 'ԯ', 'n' }, { 'Ա', 'm' },
        // Greek lookalikes
        { 'α', 'a' }, { 'β', 'b' }, { 'ε', 'e' }, { 'ζ', 'z' }, { 'η', 'h' }, { 'ι', 'i' },
        { 'κ', 'k' }, { 'μ', 'm' }, { 'ν', 'v' }, { 'ο', 'o' }, { 'ρ', 'p' }, { 'τ', 't' },
        { 'υ', 'y' }, { 'χ', 'x' }, { 'ω', 'w' }, { 'Α', 'A' }, { 'Β', 'B' }, { 'Ε', 'E' },
        { 'Ζ', 'Z' }, { 'Η', 'H' }, { 'Ι', 'I' }, { 'Κ', 'K' }, { 'Μ', 'M' }, { 'Ν', 'N' },
        { 'Ο', 'O' }, { 'Ρ', 'P' }, { 'Τ', 'T' }, { 'Υ', 'Y' }, { 'Χ', 'X' }, { 'Ω', 'W' },
        // Armenian lookalikes
        { 'ա', 'a' }, { 'բ', 'b' }, { 'ց', 'c' }, { 'դ', 'd' }, { 'ե', 'e' }, { 'ֆ', 'f' },
        { 'հ', 'h' }, { 'ի', 'i' }, { 'յ', 'y' }, { 'կ', 'k' }, { 'լ', 'l' },
        { 'մ', 'm' }, { 'ն', 'n' }, { 'ո', 'o' }, { 'պ', 'p' }, { 'ր', 'r' }, { 'ս', 's' },
        { 'տ', 't' }, { 'ւ', 'u' }, { 'խ', 'x' }, { 'ყ', 'q' }, { 'զ', 'z' }
    };

    // Regex for Punycode (xn--)
    private static readonly Regex PunycodeRegex = new(@"^xn--", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for IP address in URL
    private static readonly Regex IpAddressRegex = new(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.Compiled);

    // Regex for excessive subdomains (subdomain.subdomain.domain.tld)
    private static readonly Regex ExcessiveSubdomainsRegex = new(@"^(?:[a-z0-9-]+\.){4,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for suspicious patterns in path (random strings, base64-like)
    private static readonly Regex SuspiciousPathRegex = new(@"/(?:[a-zA-Z0-9+/]{20,}={0,2}|[a-f0-9]{32,})", RegexOptions.Compiled);

    /// <summary>Result of link analysis.</summary>
    public sealed record AnalysisResult(
        bool IsSuspicious,
        IReadOnlyList<Warning> Warnings,
        int RiskScore // 0-100
    );

    /// <summary>A specific warning about the link.</summary>
    public sealed record Warning(
        WarningType Type,
        string Message,
        string Detail
    );

    /// <summary>Type of warning.</summary>
    public enum WarningType
    {
        HomographAttack,
        UrlShortener,
        SuspiciousTld,
        BrandImpersonation,
        IpAddress,
        ExcessiveSubdomains,
        PunycodeDomain,
        SuspiciousPath,
        MismatchedDomain, // Displayed text vs actual URL
        MissingHttps
    }

    /// <summary>
    /// Analyzes a URL for suspicious characteristics.
    /// </summary>
    public static AnalysisResult Analyze(string url, string? displayedText = null)
    {
        var warnings = new List<Warning>();
        int riskScore = 0;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            warnings.Add(new Warning(WarningType.MismatchedDomain, "Invalid URL format", "The link is not a valid absolute URL."));
            return new AnalysisResult(true, warnings, 100);
        }

        // Check HTTPS
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new Warning(WarningType.MissingHttps, "Not a secure connection", "The link uses HTTP instead of HTTPS. Data sent over this connection can be intercepted."));
            riskScore += 15;
        }

        string host = uri.Host.ToLowerInvariant();

        // Check for IP address instead of domain
        if (IpAddressRegex.IsMatch(host))
        {
            warnings.Add(new Warning(WarningType.IpAddress, "Direct IP address", "The link uses an IP address instead of a domain name. This is often used to hide the true destination."));
            riskScore += 25;
        }

        // Check for Punycode (xn--)
        if (PunycodeRegex.IsMatch(host))
        {
            warnings.Add(new Warning(WarningType.PunycodeDomain, "Internationalized domain name (Punycode)", "The domain uses Punycode encoding (xn--). This can hide homograph attacks where characters look like familiar letters but are from different scripts."));
            riskScore += 30;
        }

        // Check for homograph attacks
        var homographResult = CheckHomograph(host);
        if (homographResult.found)
        {
            warnings.Add(new Warning(WarningType.HomographAttack, "Possible homograph attack", $"The domain '{host}' contains characters from non-Latin scripts that look like Latin letters: {homographResult.details}. This is a common phishing technique."));
            riskScore += 35;
        }

        // Check for URL shortener
        if (IsUrlShortener(host))
        {
            warnings.Add(new Warning(WarningType.UrlShortener, "URL shortener detected", $"The domain '{host}' is a known URL shortener. These hide the true destination and are frequently used in phishing and malware distribution."));
            riskScore += 30;
        }

        // Check for suspicious TLD
        var tld = GetTld(host);
        if (SuspiciousTlds.Contains(tld))
        {
            warnings.Add(new Warning(WarningType.SuspiciousTld, "Suspicious top-level domain", $"The domain uses '.{tld}', which is commonly associated with spam, phishing, and malware. Legitimate businesses rarely use this TLD."));
            riskScore += 20;
        }

        // Check for brand impersonation
        var brandResult = CheckBrandImpersonation(host);
        if (brandResult.found)
        {
            warnings.Add(new Warning(WarningType.BrandImpersonation, "Possible brand impersonation", $"The domain '{host}' appears to imitate '{brandResult.brand}' (e.g., {brandResult.example}). This is a common phishing technique."));
            riskScore += 40;
        }

        // Check for excessive subdomains
        if (ExcessiveSubdomainsRegex.IsMatch(host))
        {
            warnings.Add(new Warning(WarningType.ExcessiveSubdomains, "Excessive subdomains", $"The domain '{host}' has an unusual number of subdomains. This can be used to make the URL look like a legitimate domain at a glance."));
            riskScore += 15;
        }

        // Check path for suspicious patterns
        if (SuspiciousPathRegex.IsMatch(uri.AbsolutePath))
        {
            warnings.Add(new Warning(WarningType.SuspiciousPath, "Suspicious URL path", "The URL path contains patterns commonly used in tracking, obfuscation, or exploit attempts (very long random strings or encoded data)."));
            riskScore += 10;
        }

        // Check if displayed text differs from actual URL (mismatch)
        if (displayedText is not null && !string.IsNullOrWhiteSpace(displayedText))
        {
            if (TryExtractUrl(displayedText, out var displayedUri))
            {
                if (!displayedUri.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(new Warning(WarningType.MismatchedDomain, "Link text doesn't match destination", $"The link shows '{displayedUri.Host}' but actually goes to '{uri.Host}'. This is a classic phishing technique."));
                    riskScore += 50;
                }
            }
        }

        // Cap risk score
        riskScore = Math.Min(riskScore, 100);

        return new AnalysisResult(warnings.Count > 0, warnings, riskScore);
    }

    /// <summary>
    /// Checks if a domain is a known URL shortener.
    /// </summary>
    public static bool IsUrlShortener(string host) => UrlShorteners.Contains(host.ToLowerInvariant());

    /// <summary>
    /// Extracts the TLD from a hostname.
    /// </summary>
    private static string GetTld(string host)
    {
        var parts = host.Split('.');
        return parts.Length > 0 ? parts[^1] : host;
    }

    /// <summary>
    /// Checks for homograph attacks in the domain.
    /// </summary>
    private static (bool found, string details) CheckHomograph(string host)
    {
        var suspiciousChars = new List<string>();
        foreach (char c in host)
        {
            if (HomographMap.ContainsKey(c))
            {
                suspiciousChars.Add($"'{c}' (U+{(int)c:X4} looks like '{HomographMap[c]}')");
            }
        }
        return (suspiciousChars.Count > 0, string.Join(", ", suspiciousChars));
    }

    /// <summary>
    /// Checks for brand impersonation (typosquatting, subdomain spoofing).
    /// </summary>
    private static (bool found, string brand, string example) CheckBrandImpersonation(string host)
    {
        foreach (var brand in MajorBrands)
        {
            // Exact match in subdomain (e.g., google.com.phishing-site.com)
            if (host.Contains($".{brand}.") || host.StartsWith($"{brand}.") || host.EndsWith($".{brand}"))
            {
                // But allow legitimate subdomains (e.g., drive.google.com, mail.google.com)
                var parts = host.Split('.');
                if (parts.Length >= 2 && parts[^2] == brand && parts[^1].Length <= 3) continue; // e.g., google.com, google.co.uk
                if (parts.Length >= 3 && parts[^3] == brand && parts[^2].Length <= 3) continue; // e.g., google.com.au

                // This is likely impersonation
                return (true, brand, $"{brand}.{GetTld(host)}");
            }

            // Typosquatting: edit distance 1-2 from brand
            if (IsTyposquatting(host, brand))
            {
                return (true, brand, $"{brand} → {host}");
            }
        }
        return (false, "", "");
    }

    /// <summary>
    /// Simple typosquatting detection (Levenshtein distance <= 2).
    /// </summary>
    private static bool IsTyposquatting(string host, string brand)
    {
        // Check host without TLD
        var hostParts = host.Split('.');
        var domain = hostParts.Length > 1 ? string.Join(".", hostParts.Take(hostParts.Length - 1)) : host;
        var brandParts = brand.Split('.');
        var brandDomain = brandParts.Length > 1 ? string.Join(".", brandParts.Take(brandParts.Length - 1)) : brand;

        if (domain.Length < 3 || brandDomain.Length < 3) return false;

        int distance = LevenshteinDistance(domain, brandDomain);
        return distance > 0 && distance <= 2;
    }

    /// <summary>
    /// Computes Levenshtein distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    /// <summary>
    /// Tries to extract a URL from displayed text.
    /// </summary>
    private static bool TryExtractUrl(string text, out Uri? uri)
    {
        uri = null;
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var result) &&
            (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps))
        {
            uri = result;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets a short summary for UI display.
    /// </summary>
    public static string GetSummary(AnalysisResult result)
    {
        if (!result.IsSuspicious)
            return "✓ Link appears safe";

        var topWarning = result.Warnings.OrderByDescending(w => GetWarningSeverity(w.Type)).First();
        return $"⚠ {topWarning.Message} (Risk: {result.RiskScore}%)";
    }

    private static int GetWarningSeverity(WarningType type) => type switch
    {
        WarningType.MismatchedDomain => 10,
        WarningType.BrandImpersonation => 9,
        WarningType.HomographAttack => 8,
        WarningType.UrlShortener => 7,
        WarningType.PunycodeDomain => 7,
        WarningType.IpAddress => 6,
        WarningType.SuspiciousTld => 5,
        WarningType.ExcessiveSubdomains => 4,
        WarningType.SuspiciousPath => 3,
        WarningType.MissingHttps => 2,
        _ => 1
    };
}