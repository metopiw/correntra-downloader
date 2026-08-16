namespace Correntra.Infrastructure.Ipc;

public static class AgentPipeNames
{
    public static string ForCurrentUser() => CurrentUserPipeNames.For("Agent");
}
