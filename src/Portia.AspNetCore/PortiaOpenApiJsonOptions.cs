using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

sealed class PortiaOpenApiJsonOptions(JsonSerializerOptions portia) : IConfigureOptions<JsonOptions>
{
    public void Configure(JsonOptions options)
    {
        var target = options.SerializerOptions;
        target.AllowOutOfOrderMetadataProperties = portia.AllowOutOfOrderMetadataProperties;
        target.AllowTrailingCommas = portia.AllowTrailingCommas;
        target.DefaultBufferSize = portia.DefaultBufferSize;
        target.Encoder = portia.Encoder;
        target.IgnoreReadOnlyFields = portia.IgnoreReadOnlyFields;
        target.IgnoreReadOnlyProperties = portia.IgnoreReadOnlyProperties;
        target.IncludeFields = portia.IncludeFields;
        target.MaxDepth = portia.MaxDepth;
        target.NewLine = portia.NewLine;
        target.PropertyNamingPolicy = portia.PropertyNamingPolicy;
        target.DictionaryKeyPolicy = portia.DictionaryKeyPolicy;
        target.PropertyNameCaseInsensitive = portia.PropertyNameCaseInsensitive;
        target.NumberHandling = portia.NumberHandling;
        target.DefaultIgnoreCondition = portia.DefaultIgnoreCondition;
        target.PreferredObjectCreationHandling = portia.PreferredObjectCreationHandling;
        target.ReadCommentHandling = portia.ReadCommentHandling;
        target.ReferenceHandler = portia.ReferenceHandler;
        target.RespectNullableAnnotations = portia.RespectNullableAnnotations;
        target.RespectRequiredConstructorParameters = portia.RespectRequiredConstructorParameters;
        target.UnknownTypeHandling = portia.UnknownTypeHandling;
        target.UnmappedMemberHandling = portia.UnmappedMemberHandling;
        target.WriteIndented = portia.WriteIndented;
        target.TypeInfoResolver = portia.TypeInfoResolver;
        target.Converters.Clear();
        foreach (var converter in portia.Converters)
            target.Converters.Add(converter);
    }
}
