using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// HTTP API 服务配置
/// </summary>
[Serializable]
public partial class ApiConfig : ObservableObject
{
    /// <summary>
    /// 是否启用 HTTP API 服务
    /// </summary>
    [ObservableProperty]
    private bool _apiEnabled = false;

    /// <summary>
    /// API 服务端口
    /// </summary>
    [ObservableProperty]
    private int _apiPort = 20226;

    /// <summary>
    /// API 访问令牌（留空则不需要认证）
    /// </summary>
    [ObservableProperty]
    private string _apiToken = string.Empty;
}
