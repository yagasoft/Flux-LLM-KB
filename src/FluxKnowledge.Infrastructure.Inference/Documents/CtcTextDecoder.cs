namespace FluxKnowledge.Infrastructure.Inference.Documents;

public sealed record CtcTextDecoding(string Text, float MeanConfidence);

public static class CtcTextDecoder
{
    public static CtcTextDecoding Decode(
        float[,] probabilities,
        IReadOnlyList<string> characters,
        bool hasUnknownClass = false)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentNullException.ThrowIfNull(characters);
        if (probabilities.GetLength(1) != characters.Count + 1 + (hasUnknownClass ? 1 : 0))
        {
            throw new ArgumentException("CTC probabilities do not match the supplied character dictionary.", nameof(probabilities));
        }

        var text = new System.Text.StringBuilder();
        var confidenceTotal = 0f;
        var decodedCount = 0;
        var previousClass = -1;
        for (var timeStep = 0; timeStep < probabilities.GetLength(0); timeStep++)
        {
            var bestClass = 0;
            var bestProbability = probabilities[timeStep, 0];
            for (var classIndex = 1; classIndex < probabilities.GetLength(1); classIndex++)
            {
                if (probabilities[timeStep, classIndex] <= bestProbability) continue;
                bestClass = classIndex;
                bestProbability = probabilities[timeStep, classIndex];
            }

            if (bestClass > 0 && bestClass <= characters.Count && bestClass != previousClass)
            {
                text.Append(characters[bestClass - 1]);
                confidenceTotal += bestProbability;
                decodedCount++;
            }

            previousClass = bestClass;
        }

        return new CtcTextDecoding(
            text.ToString(),
            decodedCount == 0 ? 0 : confidenceTotal / decodedCount);
    }
}
