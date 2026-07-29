namespace AsteriaScale.RankerWorker;

internal static class RankerOutputProbabilities
{
    internal static float[] ExtractPositiveClass(
        ReadOnlySpan<float> logits,
        int batchSize,
        int outputClassCount,
        int positiveLabelIndex)
    {
        if (outputClassCount < 2 ||
            positiveLabelIndex < 0 ||
            positiveLabelIndex >= outputClassCount ||
            logits.Length != batchSize * outputClassCount)
        {
            throw new RankerException(
                "invalid_model_output",
                $"Expected {outputClassCount} class logits per candidate with positive " +
                $"label {positiveLabelIndex}, received {logits.Length} values for " +
                $"{batchSize} candidates.");
        }

        var scores = new float[batchSize];
        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var offset = batchIndex * outputClassCount;
            var maximumLogit = double.NegativeInfinity;
            for (var labelIndex = 0; labelIndex < outputClassCount; labelIndex++)
            {
                var logit = logits[offset + labelIndex];
                if (!float.IsFinite(logit))
                {
                    throw new RankerException(
                        "invalid_model_output",
                        "The model returned a non-finite score.");
                }

                maximumLogit = Math.Max(maximumLogit, logit);
            }

            var denominator = 0.0;
            for (var labelIndex = 0; labelIndex < outputClassCount; labelIndex++)
            {
                denominator += Math.Exp(logits[offset + labelIndex] - maximumLogit);
            }

            scores[batchIndex] = (float)(
                Math.Exp(logits[offset + positiveLabelIndex] - maximumLogit) /
                denominator);
        }

        return scores;
    }
}
