namespace DirForge.Services;

public static class GlobMatcher
{
    public static bool IsSimpleWildcardMatch(string value, string pattern, bool ignoreCase)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' ||
                 CharsMatch(pattern[patternIndex], value[valueIndex], ignoreCase)))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex;
                patternIndex++;
                matchIndex = valueIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                matchIndex++;
                valueIndex = matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool CharsMatch(char left, char right, bool ignoreCase)
    {
        if (ignoreCase)
        {
            return char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
        }

        return left == right;
    }
}
