using Microsoft.Extensions.Logging;
using Takam.Core.Configuration;

namespace Takam.Core.Embeddings;

public static class EmbeddingFactory
{
    /// <summary>
    /// Charge le modèle s'il est configuré et présent ; sinon renvoie null et l'application fonctionne
    /// en recherche par mots seulement (dégradation contrôlée, signalée dans les logs et /api/health).
    /// </summary>
    public static IEmbeddingService? TryCreate(EmbeddingOptions options, ILogger logger)
    {
        if (!options.Enabled)
        {
            logger.LogWarning("Embeddings désactivés par configuration : recherche par mots seulement.");
            return null;
        }
        var model = RepoPaths.Resolve(options.ModelPath);
        var spm = RepoPaths.Resolve(options.SentencePiecePath);
        if (!File.Exists(model) || !File.Exists(spm))
        {
            logger.LogWarning("Modèle d'embeddings absent ({Model}) : recherche par mots seulement. Lancer scripts/download-model.ps1.", model);
            return null;
        }
        try
        {
            return new E5OnnxEmbeddingService(model, spm);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec du chargement du modèle d'embeddings : recherche par mots seulement.");
            return null;
        }
    }
}
