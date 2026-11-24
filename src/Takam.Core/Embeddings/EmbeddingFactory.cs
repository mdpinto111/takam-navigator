using Microsoft.Extensions.Logging;
using Takam.Core.Configuration;

namespace Takam.Core.Embeddings;

public static class EmbeddingFactory
{
    /// <summary>
    /// Loads the model if it is configured and present; otherwise returns null and the application runs
    /// with keyword search only (controlled degradation, reported in the logs and /api/health).
    /// </summary>
    public static IEmbeddingService? TryCreate(EmbeddingOptions options, ILogger logger)
    {
        if (!options.Enabled)
        {
            logger.LogWarning("Embeddings disabled by configuration: keyword search only.");
            return null;
        }
        var model = RepoPaths.Resolve(options.ModelPath);
        var spm = RepoPaths.Resolve(options.SentencePiecePath);
        if (!File.Exists(model) || !File.Exists(spm))
        {
            logger.LogWarning("Embedding model missing ({Model}): keyword search only. Run scripts/download-model.ps1.", model);
            return null;
        }
        try
        {
            return new E5OnnxEmbeddingService(model, spm);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the embedding model: keyword search only.");
            return null;
        }
    }
}
