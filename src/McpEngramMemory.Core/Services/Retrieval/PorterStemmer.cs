namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Lightweight Porter stemmer for BM25 tokenization.
/// Reduces morphological variants to a common root form to improve recall
/// (e.g., "encrypting" and "encryption" both stem to "encrypt").
/// Implements Porter's algorithm steps 1-3 which handle the most impactful suffixes.
/// </summary>
public static class PorterStemmer
{
    private static readonly (string Suffix, string Replacement)[] Step2Suffixes =
    [
        ("ational", "ate"), ("tional", "tion"), ("enci", "ence"),
        ("anci", "ance"), ("izer", "ize"), ("abli", "able"),
        ("alli", "al"), ("entli", "ent"), ("eli", "e"),
        ("ousli", "ous"), ("ization", "ize"), ("ation", "ate"),
        ("ator", "ate"), ("alism", "al"), ("iveness", "ive"),
        ("fulness", "ful"), ("ousness", "ous"), ("aliti", "al"),
        ("iviti", "ive"), ("biliti", "ble"),
        // IR-critical: normalize -tion to match -ting stems
        // "encryption" → "encrypt", matching "encrypting" → "encrypt"
        ("tion", "t"),
    ];

    private static readonly (string Suffix, string Replacement)[] Step3Suffixes =
    [
        ("icate", "ic"), ("ative", ""), ("alize", "al"),
        ("iciti", "ic"), ("ical", "ic"), ("ful", ""), ("ness", ""),
    ];

    /// <summary>
    /// Stem a single lowercase token using Porter's algorithm (steps 1-3).
    /// Returns the stemmed form, or the original token if no rules apply.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length < 3)
            return word;

        // Step 1a, 1b, 1c: plurals, -ed/-ing, terminal y -> i
        word = Step1a(word);
        word = Step1b(word);
        word = Step1c(word);

        // Step 2: -tion, -enci, -biliti, etc.
        word = Step2(word);

        // Step 3: -ful, -ness, -iciti, etc.
        word = Step3(word);

        return word;
    }

    private static string Step1a(string word)
    {
        if (word.EndsWith("sses", StringComparison.Ordinal))
            return word[..^2]; // sses -> ss
        if (word.EndsWith("ies", StringComparison.Ordinal))
            return word[..^2]; // ies -> i  (but we want at least 3 chars)
        if (word.EndsWith("ss", StringComparison.Ordinal))
            return word; // ss -> ss (no change)
        if (word.EndsWith('s') && word.Length > 3)
            return word[..^1]; // s -> (remove)
        return word;
    }

    private static string Step1b(string word)
    {
        if (word.EndsWith("eed", StringComparison.Ordinal))
        {
            // eed -> ee if stem has measure > 0
            if (Measure(word.AsSpan(0, word.Length - 3)) > 0)
                return word[..^1]; // eed -> ee
            return word;
        }

        string? modified = null;
        if (word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 4 && ContainsVowel(word.AsSpan(0, word.Length - 3)))
        {
            modified = word[..^3]; // ing -> (remove)
        }
        else if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 3 && ContainsVowel(word.AsSpan(0, word.Length - 2)))
        {
            modified = word[..^2]; // ed -> (remove)
        }

        if (modified is not null)
        {
            // Post-processing: fix up the stem
            if (modified.EndsWith("at", StringComparison.Ordinal) ||
                modified.EndsWith("bl", StringComparison.Ordinal) ||
                modified.EndsWith("iz", StringComparison.Ordinal))
                return modified + "e";
            if (modified.Length >= 2 && IsDoubleConsonant(modified) &&
                modified[^1] is not 'l' and not 's' and not 'z')
                return modified[..^1];
            if (Measure(modified) == 1 && EndsWithCVC(modified))
                return modified + "e";
            return modified;
        }

        return word;
    }

    private static string Step1c(string word)
    {
        if (word.EndsWith('y') && word.Length > 1 && ContainsVowel(word.AsSpan(0, word.Length - 1)))
            return string.Concat(word.AsSpan(0, word.Length - 1), "i");
        return word;
    }

    private static string Step2(string word)
    {
        // Only apply if stem has measure > 0
        foreach (var (suffix, replacement) in Step2Suffixes)
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal))
            {
                var stem = word.AsSpan(0, word.Length - suffix.Length);
                if (Measure(stem) > 0)
                    return string.Concat(stem, replacement);
                return word;
            }
        }

        return word;
    }

    private static string Step3(string word)
    {
        foreach (var (suffix, replacement) in Step3Suffixes)
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal))
            {
                var stem = word.AsSpan(0, word.Length - suffix.Length);
                if (Measure(stem) > 0)
                    return string.Concat(stem, replacement);
                return word;
            }
        }

        return word;
    }

    // Measure (m) = number of VC sequences in the word
    private static int Measure(ReadOnlySpan<char> word)
    {
        int m = 0;
        int i = 0;
        int len = word.Length;

        // Skip initial consonants
        while (i < len && !IsVowel(word, i)) i++;
        // Count VC sequences
        while (i < len)
        {
            while (i < len && IsVowel(word, i)) i++;
            if (i >= len) break;
            while (i < len && !IsVowel(word, i)) i++;
            m++;
        }
        return m;
    }

    private static bool ContainsVowel(ReadOnlySpan<char> word)
    {
        for (int i = 0; i < word.Length; i++)
            if (IsVowel(word, i)) return true;
        return false;
    }

    private static bool IsVowel(ReadOnlySpan<char> word, int i)
    {
        char c = word[i];
        if (c is 'a' or 'e' or 'i' or 'o' or 'u') return true;
        if (c == 'y' && i > 0 && !IsVowel(word, i - 1)) return true;
        return false;
    }

    private static bool IsDoubleConsonant(ReadOnlySpan<char> word)
    {
        if (word.Length < 2) return false;
        return word[^1] == word[^2] && !IsVowel(word, word.Length - 1);
    }

    private static bool EndsWithCVC(ReadOnlySpan<char> word)
    {
        if (word.Length < 3) return false;
        int len = word.Length;
        return !IsVowel(word, len - 1) &&
               IsVowel(word, len - 2) &&
               !IsVowel(word, len - 3) &&
               word[^1] is not 'w' and not 'x' and not 'y';
    }
}
