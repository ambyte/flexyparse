namespace FlexyParse.Nemotron;

/// <summary>Model context bounds and conservative, token-level detection of periodic output tails.</summary>
internal static class NemotronGenerationGuard
{
    internal const int ContextLength = 16384;

    internal static int GetGenerationSteps(int prefixLength)
    {
        if (prefixLength < 1 || prefixLength > ContextLength)
            throw new InvalidDataException("Nemotron visual prefix and prompt exceed the model context.");

        // Prefill predicts the first token; the last valid decoder input may still predict EOS.
        return ContextLength - prefixLength + 1;
    }

    internal static void ThrowIfRepetitive(IReadOnlyList<int> tokens, NemotronOptions options)
    {
        if (tokens.Count < options.RepetitionDetectionMinTokens) return;

        var maxPeriod = Math.Min(options.RepetitionDetectionMaxPeriod,
            tokens.Count / options.RepetitionDetectionMinRepeats);
        for (var period = 1; period <= maxPeriod; period++)
        {
            var requiredTokens = Math.Max(options.RepetitionDetectionMinTokens,
                period * options.RepetitionDetectionMinRepeats);
            if (tokens.Count < requiredTokens) continue;

            // Compare the complete tail, not just hashes or isolated repeated n-grams.
            // Checking backwards usually rejects ordinary prose/table rows immediately.
            var first = tokens.Count - requiredTokens;
            var index = tokens.Count - 1;
            while (index >= first + period && tokens[index] == tokens[index - period]) index--;
            if (index < first + period)
                throw new NemotronRepetitionException(
                    $"Nemotron generation detected a repetition loop (period {period}, repeated tail {requiredTokens} tokens, generated {tokens.Count} tokens); incomplete OCR was discarded.")
                {
                    GeneratedTokenCount = tokens.Count,
                    RepetitionPeriod = period,
                    RepeatedTokenIds = tokens.Skip(tokens.Count - period).ToArray()
                };
        }
    }
}
