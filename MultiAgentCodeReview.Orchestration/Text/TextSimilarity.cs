using System.Text.RegularExpressions;

namespace MultiAgentCodeReview.Orchestration.Text;

/// <summary>
/// Plain lexical similarity used wherever two short pieces of review text must be compared (merging
/// near-duplicate findings, matching findings to human comments in the benchmark): lower-cased,
/// stop-word-filtered, lightly stemmed, camelCase-aware tokens compared with the Dice coefficient,
/// 2|A∩B| / (|A|+|B|). Deliberately simple and deterministic: it catches shared vocabulary, not paraphrase.
/// </summary>
public static class TextSimilarity
{
    /// <summary>Two texts must share at least this many tokens, so one coincidental word never counts.</summary>
    public const int MinSharedTokens = 2;

    // Splits "SqlCommand" -> Sql, Command; "userId" -> user, Id; "HTTPClient" -> HTTP, Client; keeps digits.
    private static readonly Regex TokenRegex = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "an", "and", "or", "but", "if", "then", "else", "of", "to", "in", "on", "at", "by", "for", "with",
        "from", "into", "as", "is", "are", "was", "were", "be", "been", "being", "it", "its", "this", "that",
        "these", "those", "there", "here", "which", "who", "what", "when", "where", "why", "how", "not", "no",
        "can", "could", "should", "would", "may", "might", "must", "will", "shall", "do", "does", "did", "has",
        "have", "had", "also", "just", "only", "very", "more", "most", "some", "any", "all", "each", "per",
        "via", "than", "so", "such", "too", "use", "used", "using", "consider", "instead", "make", "ensure",
        "please", "code", "line", "method", "function", "file", "class", "you", "your", "we", "our",
        "think", "maybe", "probably", "possible", "possibly", "seems", "looks", "like", "nit"
    };

    // Longest suffix first. Enough to unify "injection/injected/injecting", "queries/query",
    // "concatenation/concatenating/concatenated/concatenate"; not a real stemmer. ("-ation" is deliberately
    // not a suffix: stripping only "-ion" leaves "concatenat", which is what "concatenate" stems to.)
    private static readonly string[] Suffixes =
        { "ions", "ion", "ings", "ing", "ied", "ies", "ed", "es", "s" };

    public static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TokenRegex.Matches(text))
        {
            var t = m.Value.ToLowerInvariant();
            if (Stopwords.Contains(t))
                continue;
            t = Stem(t);
            if (t.Length >= 2 && !Stopwords.Contains(t))
                tokens.Add(t);
        }
        return tokens;
    }

    private static string Stem(string t)
    {
        foreach (var suffix in Suffixes)
        {
            if (t.Length - suffix.Length < 3 || !t.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var stem = t[..^suffix.Length];
            if (suffix is "ies" or "ied")
                return stem + "y";
            return stem.Length > 3 && stem.EndsWith('e') ? stem[..^1] : stem;
        }
        return t.Length > 4 && t.EndsWith('e') ? t[..^1] : t;
    }

    public static double Dice(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var shared = a.Count(b.Contains);
        if (shared < Math.Min(MinSharedTokens, Math.Min(a.Count, b.Count)))
            return 0;

        return 2.0 * shared / (a.Count + b.Count);
    }
}
