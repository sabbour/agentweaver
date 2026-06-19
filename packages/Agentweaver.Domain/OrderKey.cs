using System.Text;

namespace Agentweaver.Domain;

/// <summary>
/// Pure helper that generates lexicographic fractional rank keys over the lowercase alphabet
/// a-z. "Top of bucket" = smallest key = highest priority. <see cref="Between"/> returns a key
/// strictly between two neighbours (either may be null to mean "before first" / "after last"),
/// extending digits on demand when neighbours are adjacent so a single reorder needs only one row
/// update. Keys are opaque to clients.
/// </summary>
public static class OrderKey
{
    private const int Base = 26;
    private const char Zero = 'a';

    /// <summary>
    /// Returns a key strictly between <paramref name="lo"/> and <paramref name="hi"/>. A null
    /// <paramref name="lo"/> means "before the first item"; a null <paramref name="hi"/> means
    /// "after the last item". Throws when <paramref name="lo"/> is not strictly less than
    /// <paramref name="hi"/>, or when the requested gap cannot be represented (only the genuine
    /// "before the smallest possible key" case).
    /// </summary>
    public static string Between(string? lo, string? hi)
    {
        if (lo is not null && hi is not null && string.CompareOrdinal(lo, hi) >= 0)
            throw new ArgumentException($"OrderKey.Between requires lo < hi (lo='{lo}', hi='{hi}').");

        var res = new StringBuilder();
        var i = 0;
        var hiOpen = hi is null;   // hi == null => +infinity at every position
        while (true)
        {
            var a = i < (lo?.Length ?? 0) ? lo![i] - Zero : -1;            // -1 = below 'a' (open below / exhausted)
            var b = hiOpen ? Base : (i < (hi?.Length ?? 0) ? hi![i] - Zero : -1);

            if (a == b)
            {
                if (a < 0)
                    throw new ArgumentException("OrderKey.Between: no key exists in the requested gap.");
                res.Append((char)(Zero + a));
                i++;
                continue;
            }

            // Prefixes equal so far, so a < b holds.
            var mid = (a + b) / 2;
            if (mid > a && mid < b && mid >= 0)
            {
                res.Append((char)(Zero + mid));
                return res.ToString();
            }

            // Neighbours are adjacent: no integer strictly between a and b at this position.
            if (a >= 0)
            {
                // Follow lo downward; room opens above for the remaining digits.
                res.Append((char)(Zero + a));
                hiOpen = true;
                i++;
                continue;
            }

            // a == -1 (open below) and b == 0: match hi's 'a' and keep searching below hi.
            res.Append(Zero);
            i++;
        }
    }
}
