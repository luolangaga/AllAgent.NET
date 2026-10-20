namespace AllAgent.NET.Core.Runtime;

public static class DelegatedPermissions
{
    public static PermissionMode Restrict(PermissionMode grant, PermissionMode current) =>
        grant == PermissionMode.Ask || current == PermissionMode.Ask ? PermissionMode.Ask :
        grant == PermissionMode.Restricted || current == PermissionMode.Restricted ? PermissionMode.Restricted : PermissionMode.Full;
}
