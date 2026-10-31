using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Miguel.Server.Audit;

public static partial class AuditRedactor
{
    private static readonly HashSet<string> SensitiveNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "enrollmentToken", "deviceKey",
        "credential", "authorization", "connectionString", "privateKey",
        "apiKey", "token"
    };

    public static JsonElement? RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new
            {
                protectedDetail = "El detalle no pudo representarse de forma segura."
            });
        }

        RedactNode(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (string name in objectNode.Select(item => item.Key).ToArray())
            {
                if (IsSensitiveName(name))
                {
                    objectNode[name] = "[PROTEGIDO]";
                    continue;
                }

                JsonNode? child = objectNode[name];
                if (child is JsonValue value &&
                    value.TryGetValue<string>(out string? text))
                {
                    objectNode[name] = RedactText(text);
                }
                else
                {
                    RedactNode(child);
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            for (int index = 0; index < arrayNode.Count; index++)
            {
                JsonNode? child = arrayNode[index];
                if (child is JsonValue value &&
                    value.TryGetValue<string>(out string? text))
                {
                    arrayNode[index] = RedactText(text);
                }
                else
                {
                    RedactNode(child);
                }
            }
        }
    }

    public static string RedactText(string value) =>
        InstallerTokenFileName().Replace(
            value,
            "ATLAS-Setup_[token-protegido].exe");

    private static bool IsSensitiveName(string name)
    {
        if (SensitiveNames.Contains(name))
        {
            return true;
        }

        string normalized = name.Replace("_", string.Empty)
            .Replace("-", string.Empty);
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("enrollmenttoken", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("devicekey", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"ATLAS-Setup_[A-Za-z0-9_-]{20,}\.exe",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerTokenFileName();
}
