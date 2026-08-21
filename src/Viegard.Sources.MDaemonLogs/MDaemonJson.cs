using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viegard.Sources.MDaemonLogs;

public static class MDaemonJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
