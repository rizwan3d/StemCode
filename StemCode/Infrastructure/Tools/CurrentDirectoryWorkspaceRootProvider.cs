using StemCode.Application.Abstractions;

namespace StemCode.Infrastructure.Tools;

internal sealed class CurrentDirectoryWorkspaceRootProvider : IWorkspaceRootProvider
{
    public string GetWorkspaceRoot()
    {
        return WorkspaceRootScope.Current ?? Directory.GetCurrentDirectory();
    }
}
