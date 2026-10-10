using System.Text.RegularExpressions;

namespace Agentweaver.Orchestrator.Core;

public static class WorkflowTriggerRegexPolicy
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions SafeRegexOptions =
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static bool TryValidatePattern(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Pattern is required.";
            return false;
        }

        if (!IsSupportedSafeSubset(pattern, out error))
            return false;

        try
        {
            _ = new Regex(pattern, SafeRegexOptions, MatchTimeout);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = $"Pattern is not a valid safe regular expression: {exception.Message}";
            return false;
        }
        catch (NotSupportedException exception)
        {
            error = $"Pattern is not supported by the safe regular expression engine: {exception.Message}";
            return false;
        }
    }

    public static bool IsMatch(string pattern, string input)
    {
        return TryMatch(pattern, input, out var matches, out _) && matches;
    }

    public static bool TryMatch(
        string pattern,
        string input,
        out bool matches,
        out string? error)
    {
        matches = false;
        if (!TryValidatePattern(pattern, out error))
            return false;

        try
        {
            matches = Regex.IsMatch(input, pattern, SafeRegexOptions, MatchTimeout);
            return true;
        }
        catch (RegexMatchTimeoutException exception)
        {
            error = $"Pattern matching exceeded the {MatchTimeout.TotalMilliseconds:0} ms time limit: {exception.Message}";
            return false;
        }
        catch (ArgumentException exception)
        {
            error = $"Pattern matching failed: {exception.Message}";
            return false;
        }
        catch (NotSupportedException exception)
        {
            error = $"Pattern matching is not supported by the safe regular expression engine: {exception.Message}";
            return false;
        }
    }

    private static bool IsSupportedSafeSubset(string pattern, out string? error)
    {
        error = null;
        var groups = new Stack<GroupFrame>();
        var inCharacterClass = false;
        var escaping = false;

        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (escaping)
            {
                if (char.IsDigit(character))
                {
                    error = "Backreferences are not allowed in comment_matches.";
                    return false;
                }

                if (character == 'G')
                {
                    error = "The \\G anchor is not supported in comment_matches.";
                    return false;
                }

                escaping = false;
                continue;
            }

            if (character == '\\')
            {
                escaping = true;
                continue;
            }

            if (inCharacterClass)
            {
                if (character == ']')
                    inCharacterClass = false;
                continue;
            }

            if (character == '[')
            {
                inCharacterClass = true;
                continue;
            }

            if (character == '(')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '?')
                {
                    if (index + 2 >= pattern.Length || pattern[index + 2] != ':')
                    {
                        error = "Advanced group constructs are not allowed in comment_matches.";
                        return false;
                    }

                    index += 2;
                }

                groups.Push(new GroupFrame());
                continue;
            }

            if (character == ')')
            {
                if (groups.Count == 0)
                {
                    error = "Pattern has an unmatched closing parenthesis.";
                    return false;
                }

                var group = groups.Pop();
                if (IsQuantifierStart(pattern, index + 1) && group.ContainsInnerQuantifier)
                {
                    error = "Nested quantifiers are not allowed in comment_matches.";
                    return false;
                }

                continue;
            }

            if (IsQuantifierToken(pattern, index))
            {
                foreach (var group in groups)
                    group.ContainsInnerQuantifier = true;
            }
        }

        if (escaping)
        {
            error = "Pattern ends with an incomplete escape sequence.";
            return false;
        }

        if (inCharacterClass)
        {
            error = "Pattern has an unterminated character class.";
            return false;
        }

        if (groups.Count > 0)
        {
            error = "Pattern has an unmatched opening parenthesis.";
            return false;
        }

        return true;
    }

    private static bool IsQuantifierStart(string pattern, int index) =>
        index < pattern.Length && IsQuantifierToken(pattern, index);

    private static bool IsQuantifierToken(string pattern, int index)
    {
        if (index >= pattern.Length)
            return false;

        return pattern[index] switch
        {
            '*' or '+' or '?' => true,
            '{' => IsBraceQuantifier(pattern, index),
            _ => false
        };
    }

    private static bool IsBraceQuantifier(string pattern, int index)
    {
        var cursor = index + 1;
        var sawDigit = false;
        while (cursor < pattern.Length && char.IsDigit(pattern[cursor]))
        {
            sawDigit = true;
            cursor++;
        }

        if (cursor < pattern.Length && pattern[cursor] == ',')
        {
            cursor++;
            while (cursor < pattern.Length && char.IsDigit(pattern[cursor]))
            {
                sawDigit = true;
                cursor++;
            }
        }

        return sawDigit && cursor < pattern.Length && pattern[cursor] == '}';
    }

    private sealed class GroupFrame
    {
        public bool ContainsInnerQuantifier { get; set; }
    }
}
