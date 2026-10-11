using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Serializes versioned deep evidence with concrete set restoration.</summary>
internal static class FontDeepEvidenceJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes one file's complete deep comparison evidence.</summary>
    /// <param name="evidence">The evidence to persist.</param>
    /// <returns>The JSON payload.</returns>
    public static string Serialize(FontDeepAnalysisEvidence evidence) => JsonSerializer.Serialize(evidence, Options);

    /// <summary>Restores one file's complete deep comparison evidence.</summary>
    /// <param name="payload">The stored JSON payload.</param>
    /// <returns>The restored evidence.</returns>
    public static FontDeepAnalysisEvidence Deserialize(string payload)
        => JsonSerializer.Deserialize<FontDeepAnalysisEvidence>(payload, Options)
            ?? throw new JsonException("Deep font evidence payload is empty.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ReadOnlySetConverter<string>());
        options.Converters.Add(new ReadOnlySetConverter<uint>());
        options.Converters.Add(new ReadOnlySetConverter<FontQualitySignal>());
        return options;
    }

    private sealed class ReadOnlySetConverter<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<HashSet<T>>(ref reader, options)
                ?? throw new JsonException("Deep font evidence set is null.");

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new List<T>(value), options);
    }
}
