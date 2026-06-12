using System;
using System.Collections.Generic;
using System.Linq;

namespace SecureVault;

/// <summary>
/// Simple fuzzy/substring search with typo tolerance.
/// Scores: exact match > starts-with > contains > character-sequence match.
/// </summary>
public static class FuzzySearch
{
    public static List<T> Search<T>(IEnumerable<T> items, string query, Func<T, string> getText, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return items.Take(maxResults).ToList();

        var q = query.ToLower().Trim();
        return items
            .Select(item => (item, score: Score(getText(item).ToLower(), q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => x.item)
            .ToList();
    }

    private static int Score(string text, string query)
    {
        if (text == query)                              return 1000;
        if (text.StartsWith(query))                    return 800;
        if (text.Contains(query))                      return 600;

        // Fuzzy: allow 1 transposition or substitution
        var words = text.Split(' ');
        foreach (var word in words)
        {
            if (word == query) return 700;
            if (word.StartsWith(query)) return 500;
            if (EditDistance(word, query) <= 1) return 300;
            if (EditDistance(word, query) == 2 && query.Length > 4) return 150;
        }

        // Character sequence: all query chars appear in order
        int ti = 0, qi = 0;
        while (ti < text.Length && qi < query.Length)
        { if (text[ti] == query[qi]) qi++; ti++; }
        if (qi == query.Length) return 100;

        return 0;
    }

    private static int EditDistance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3) return 99;
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i-1] == b[j-1]
                    ? d[i-1, j-1]
                    : 1 + Math.Min(d[i-1,j], Math.Min(d[i,j-1], d[i-1,j-1]));
        return d[a.Length, b.Length];
    }
}
