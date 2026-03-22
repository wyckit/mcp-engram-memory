namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Lightweight Porter stemmer for BM25 tokenization.
/// Reduces morphological variants to a common root form to improve recall
/// (e.g., "encrypting" and "encryption" both stem to "encrypt").
/// Implements Porter's algorithm steps 1-3 which handle the most impactful suffixes.
/// </summary>
public static class PorterStemmer
{
    /// <summary>
    /// Stem a single lowercase token using Porter's algorithm (steps 1-3).
    /// Returns the stemmed form, or the original token if no rules apply.
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length < 3)
            return word;

        // Step 1a: plurals and -ed/-ing
        word = Step1a(word);
        word = Step1b(word);

        // Step 2: -tion, -ness, -ment, etc.
        word = Step2(word);

        // Step 3: -ful, -ness, -ive, etc.
        word = Step3(word);

        return word;
    }

    private static string Step1a(string word)
    {
        if (word.EndsWith("sses"))
            return word[..^2]; // sses -> ss
        if (word.EndsWith("ies"))
            return word[..^2]; // ies -> i  (but we want at least 3 chars)
        if (word.EndsWith("ss"))
            return word; // ss -> ss (no change)
        if (word.EndsWith("s") && word.Length > 3)
            return word[..^1]; // s -> (remove)
        return word;
    }

    private static string Step1b(string word)
    {
        if (word.EndsWith("eed"))
        {
            // eed -> ee if stem has measure > 0
            var stem = word[..^3];
            if (Measure(stem) > 0)
                return word[..^1]; // eed -> ee
            return word;
        }

        string? modified = null;
        if (word.EndsWith("ing") && word.Length > 4 && ContainsVowel(word[..^3]))
        {
            modified = word[..^3]; // ing -> (remove)
        }
        else if (word.EndsWith("ed") && word.Length > 3 && ContainsVowel(word[..^2]))
        {
            modified = word[..^2]; // ed -> (remove)
        }

        if (modified is not null)
        {
            // Post-processing: fix up the stem
            if (modified.EndsWith("at") || modified.EndsWith("bl") || modified.EndsWith("iz"))
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

    private static string Step2(string word)
    {
        // Only apply if stem has measure > 0
        var suffixes = new (string suffix, string replacement)[]
        {
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
        };

        foreach (var (suffix, replacement) in suffixes)
        {
            if (word.EndsWith(suffix))
            {
                var stem = word[..^suffix.Length];
                if (Measure(stem) > 0)
                    return stem + replacement;
                return word;
            }
        }

        return word;
    }

    private static string Step3(string word)
    {
        var suffixes = new (string suffix, string replacement)[]
        {
            ("icate", "ic"), ("ative", ""), ("alize", "al"),
            ("iciti", "ic"), ("ical", "ic"), ("ful", ""), ("ness", ""),
        };

        foreach (var (suffix, replacement) in suffixes)
        {
            if (word.EndsWith(suffix))
            {
                var stem = word[..^suffix.Length];
                if (Measure(stem) > 0)
                    return stem + replacement;
                return word;
            }
        }

        return word;
    }

    // Measure (m) = number of VC sequences in the word
    private static int Measure(string word)
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

    private static bool ContainsVowel(string word)
    {
        for (int i = 0; i < word.Length; i++)
            if (IsVowel(word, i)) return true;
        return false;
    }

    private static bool IsVowel(string word, int i)
    {
        char c = word[i];
        if (c is 'a' or 'e' or 'i' or 'o' or 'u') return true;
        if (c == 'y' && i > 0 && !IsVowel(word, i - 1)) return true;
        return false;
    }

    private static bool IsDoubleConsonant(string word)
    {
        if (word.Length < 2) return false;
        return word[^1] == word[^2] && !IsVowel(word, word.Length - 1);
    }

    private static bool EndsWithCVC(string word)
    {
        if (word.Length < 3) return false;
        int len = word.Length;
        return !IsVowel(word, len - 1) &&
               IsVowel(word, len - 2) &&
               !IsVowel(word, len - 3) &&
               word[^1] is not 'w' and not 'x' and not 'y';
    }
}
