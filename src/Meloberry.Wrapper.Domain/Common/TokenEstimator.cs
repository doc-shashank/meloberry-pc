namespace Meloberry.Wrapper.Domain.Common;

/// <summary>Pure BPE-aware token estimator and safe Markdown fence helper.</summary>
public static class TokenEstimator
{
    /// <summary>
    /// BPE-aware deterministic token estimator (words, digits, symbols, and newlines),
    /// closely tracking modern LLM tokenizers without pulling heavy native dependencies.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == ' ' || ch == '\t' || ch == '\r')
            {
                // Single leading space attaches to the following word in BPE; count extra whitespace runs lightly.
                var run = 0;
                while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r'))
                {
                    run++;
                    i++;
                }

                if (run > 1)
                {
                    tokens += Math.Max(1, (run - 1) / 4);
                }

                continue;
            }

            if (ch == '\n')
            {
                tokens++;
                i++;
                continue;
            }

            if (char.IsLetter(ch))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                var len = i - start;
                tokens += len <= 6 ? 1 : (int)Math.Ceiling(len / 4.5);
                continue;
            }

            if (char.IsDigit(ch))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                var len = i - start;
                tokens += Math.Max(1, (int)Math.Ceiling(len / 3.0));
                continue;
            }

            // Group repeated identical punctuation (e.g. "###", "---", "```") as one token.
            var punct = ch;
            while (i < text.Length && text[i] == punct)
            {
                i++;
            }

            tokens++;
        }

        return Math.Max(1, tokens);
    }

    /// <summary>Returns a backtick fence strictly longer than any consecutive backtick run in <paramref name="content"/>.</summary>
    public static string MakeCodeFence(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "```";
        }

        var maxRun = 0;
        var run = 0;
        foreach (var ch in content)
        {
            if (ch == '`')
            {
                run++;
                if (run > maxRun) maxRun = run;
            }
            else
            {
                run = 0;
            }
        }

        return new string('`', Math.Max(3, maxRun + 1));
    }
}
