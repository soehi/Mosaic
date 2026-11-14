using System.Windows;

namespace Mosaic.Shell.Wpf;

internal static class ShellLocalization
{
    public static ResourceDictionary Dictionary(bool english) => new()
    {
        ["Shell.Tagline"] = english ? "Every task leaves a trace" : "让每一件事，有迹可循",
        ["Shell.WorkspaceStatus"] = english ? "Local workspace" : "本机工作空间",
        ["Shell.WorkspaceStatusDetail"] = english ? "Offline ready · Data stays on this PC" : "离线可用 · 数据保存在此电脑",
        ["Shell.WorkspaceStatusAccessibility"] = english ? "Local workspace, offline ready" : "本机工作空间，离线可用",
        ["Shell.WorkspaceBreadcrumb"] = english ? "MOSAIC / Workspace" : "MOSAIC / 工作空间",
        ["Shell.EmptyTitle"] = english ? "Workspace is ready" : "工作空间已就绪",
        ["Shell.EmptyDescription"] = english ? "No features are enabled. Update module configuration and restart." : "当前没有启用的功能。修改模块配置并重启后即可使用。",
        ["Shell.PageTitle"] = english ? "Workspace" : "工作空间"
    };
}
