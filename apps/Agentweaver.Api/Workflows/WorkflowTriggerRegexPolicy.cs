using System.Text.RegularExpressions;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Safety policy for <c>commentMatches</c>. Patterns are validated against a restricted regex subset
/// and executed with the non-backtracking engine plus a hard timeout.
/// </summary>
public static class WorkflowTriggerRegexPolicy
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions SafeRegexOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static bool TryValidatePattern(string pattern, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "pattern is required.";
            return false;
        }

        if (!IsSupportedSafeSubset(pattern, out error))
            return false;

        try
        {
            _ = new Regex(pattern, SafeRegexOptions, MatchTimeout);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"pattern is not a valid safe regex: {ex.Message}";
            return false;
        }
    }

    public static bool IsMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern, SafeRegexOptions, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSupportedSafeSubset(string pattern, out string? error)
    {
        error = null;
        var stack = new Stack<GroupFrame>();
        var inCharacterClass = false;
        var escaping = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];

            if (escaping)
            {
                if (char.IsDigit(ch))
                {
                    error = "pattern uses backreferences, which are not allowed in comment_matches.";
                    return false;
                }

                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (inCharacterClass)
            {
                if (ch == ']')
                    inCharacterClass = false;
                continue;
            }

            if (ch == '[')
            {
                inCharacterClass = true;
                continue;
            }

            if (ch == '(')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                {
                    if (i + 2 >= pattern.Length || pattern[i + 2] != ':')
                    {
                        error = "pattern uses an advanced group construct that is not allowed in comment_matches.";
                        return false;
                    }

                    i += 2;
                }

                stack.Push(new GroupFrame());
                continue;
            }

            if (ch == ')')
            {
                if (stack.Count == 0)
                {
                    error = "pattern has an unmatched closing parenthesis.";
                    return false;
                }

                var frame = stack.Pop();
                if (IsQuantifierStart(pattern, i + 1) && frame.ContainsInnerQuantifier)
                {
                    error = "pattern uses a quantified group that already contains a quantifier; nested quantifiers are not allowed in comment_matches.";
                    return false;
                }

                continue;
            }

            if (IsQuantifierToken(pattern, i))
            {
                foreach (var frame in stack)
                    frame.ContainsInnerQuantifier = true;
            }
        }

        if (escaping)
        {
            error = "pattern ends with an incomplete escape sequence.";
            return false;
        }

        if (inCharacterClass)
        {
            error = "pattern has an unterminated character class.";
            return false;
        }

        if (stack.Count > 0)
        {
            error = "pattern has an unmatched opening parenthesis.";
            return false;
        }

        return true;
    }

    private static bool IsQuantifierStart(string pattern, int index) =>
        index < pattern.Length && IsQuantifierToken(pattern, index);

    private static bool IsQuantifierToken(string pattern, int index)
    {
        if (index >= pattern.Length) return false;
        return pattern[index] switch
        {
            '*' or '+' or '?' => true,
            '{' => IsBraceQuantifier(pattern, index),
            _ => false,
        };
    }

    private static bool IsBraceQuantifier(string pattern, int index)
    {
        var i = index + 1;
        var sawDigit = false;
        while (i < pattern.Length && char.IsDigit(pattern[i]))
        {
            sawDigit = true;
            i++;
        }

        if (i < pattern.Length && pattern[i] == ',')
        {
            i++;
            while (i < pattern.Length && char.IsDigit(pattern[i]))
            {
                sawDigit = true;
                i++;
            }
        }

        return sawDigit && i < pattern.Length && pattern[i] == '}';
    }

    private sealed class GroupFrame
    {
        public bool ContainsInnerQuantifier { get; set; }
    }
}
