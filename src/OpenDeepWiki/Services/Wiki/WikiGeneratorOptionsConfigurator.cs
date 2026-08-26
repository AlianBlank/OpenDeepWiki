using Microsoft.Extensions.Configuration;

namespace OpenDeepWiki.Services.Wiki;

public static class WikiGeneratorOptionsConfigurator
{
    public static void Apply(WikiGeneratorOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.CatalogProviderId = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:CatalogProviderId",
            options.CatalogProviderId);
        options.CatalogModel = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:CatalogModel",
            options.CatalogModel) ?? options.CatalogModel;

        options.ContentProviderId = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:ContentProviderId",
            options.ContentProviderId);
        options.ContentModel = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:ContentModel",
            options.ContentModel) ?? options.ContentModel;

        options.TranslationProviderId = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:TranslationProviderId",
            options.TranslationProviderId);
        options.TranslationModel = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:TranslationModel",
            options.TranslationModel);

        options.CatalogFallbackModels = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:CatalogFallbackModels",
            options.CatalogFallbackModels);
        options.ContentFallbackModels = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:ContentFallbackModels",
            options.ContentFallbackModels);
        options.TranslationFallbackModels = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:TranslationFallbackModels",
            options.TranslationFallbackModels);

        options.Languages = ResolveStringValue(
            configuration,
            $"{WikiGeneratorOptions.SectionName}:Languages",
            options.Languages);
    }

    private static string? ResolveStringValue(
        IConfiguration configuration,
        string sectionKey,
        string? fallbackValue)
    {
        return !string.IsNullOrWhiteSpace(configuration[sectionKey])
            ? configuration[sectionKey]
            : fallbackValue;
    }
}
