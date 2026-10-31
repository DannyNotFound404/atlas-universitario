using System.Reflection;

namespace Miguel.Agent;

public static class AgentVersion
{
    public static string Current =>
        typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(AgentVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
