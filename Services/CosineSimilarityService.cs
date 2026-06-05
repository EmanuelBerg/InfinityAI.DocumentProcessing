namespace InfinityAI.Api.Services;

public interface ICosineSimilarityService
{
    double Calculate(
        IReadOnlyList<float> a,
        IReadOnlyList<float> b);
}

public sealed class CosineSimilarityService : ICosineSimilarityService
{
    public double Calculate(
        IReadOnlyList<float> a,
        IReadOnlyList<float> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        if (a.Count != b.Count)
            return 0;

        double dot = 0;
        double magnitudeA = 0;
        double magnitudeB = 0;

        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dot / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
    }
}