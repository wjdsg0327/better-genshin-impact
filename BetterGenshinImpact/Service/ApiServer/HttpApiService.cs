using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.ApiServer;

/// <summary>
/// HTTP API 服务，允许外部程序通过 HTTP 请求调用一条龙和配置组
/// </summary>
public class HttpApiService : IHostedService, IDisposable
{
    private readonly ILogger<HttpApiService> _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public HttpApiService(ILogger<HttpApiService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = TaskContext.Instance().Config.ApiConfig;
        if (!config.ApiEnabled)
        {
            _logger.LogInformation("HTTP API 服务未启用，跳过启动");
            return Task.CompletedTask;
        }

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new HttpListener();
            var port = config.ApiPort;
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
            _logger.LogInformation("HTTP API 服务已启动，监听端口: {Port}", port);
            _listenerTask = Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP API 服务启动失败");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HTTP API 服务正在停止");
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _listener?.Close();
        GC.SuppressFinalize(this);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), ct);
            }
            catch (HttpListenerException)
            {
                // Listener was stopped
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP API 处理请求时发生错误");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // CORS headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            // 认证检查
            var config = TaskContext.Instance().Config.ApiConfig;
            if (!string.IsNullOrEmpty(config.ApiToken))
            {
                var authHeader = request.Headers["Authorization"];
                if (string.IsNullOrEmpty(authHeader) || authHeader != $"Bearer {config.ApiToken}")
                {
                    await WriteJsonResponse(response, 401, new ApiErrorResponse("未授权访问，请提供有效的 Bearer Token"));
                    return;
                }
            }

            // 路由
            var path = request.Url?.AbsolutePath?.ToLower() ?? "";
            var method = request.HttpMethod.ToUpper();

            switch (path)
            {
                case "/api/status" when method == "GET":
                    await HandleStatus(response);
                    break;
                case "/api/startonedragon" when method == "POST":
                    await HandleStartOneDragon(request, response);
                    break;
                case "/api/startgroups" when method == "POST":
                    await HandleStartGroups(request, response);
                    break;
                case "/api/stop" when method == "POST":
                    await HandleStop(response);
                    break;
                default:
                    await WriteJsonResponse(response, 404, new ApiErrorResponse("接口不存在"));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP API 处理请求时发生错误");
            try
            {
                await WriteJsonResponse(response, 500, new ApiErrorResponse($"服务器内部错误: {ex.Message}"));
            }
            catch
            {
                // Response may already be closed
            }
        }
    }

    /// <summary>
    /// GET /api/status - 获取当前状态
    /// </summary>
    private async Task HandleStatus(HttpListenerResponse response)
    {
        var isRunning = !CancellationContext.Instance.IsCancellationRequested;
        var result = new
        {
            success = true,
            isRunning,
            message = isRunning ? "任务正在运行中" : "当前无任务运行"
        };
        await WriteJsonResponse(response, 200, result);
    }

    /// <summary>
    /// POST /api/startOneDragon - 启动一条龙
    /// Body: { "configName": "默认配置" } (可选)
    /// </summary>
    private async Task HandleStartOneDragon(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadRequestBody(request);
        var configName = body?.GetPropertyOrDefault<string>("configName");

        // 在 UI 线程上执行
        var success = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var vm = App.GetService<OneDragonFlowViewModel>();
                if (vm == null)
                {
                    return false;
                }

                // 确保配置列表已加载
                if (vm.ConfigList.Count == 0)
                {
                    vm.OnNavigatedTo();
                }

                // 指定配置名称
                if (!string.IsNullOrEmpty(configName))
                {
                    var targetConfig = vm.ConfigList.FirstOrDefault(x =>
                        string.Equals(x.Name, configName, StringComparison.Ordinal));
                    if (targetConfig != null)
                    {
                        vm.SelectedConfig = targetConfig;
                    }
                    else
                    {
                        _logger.LogWarning("未找到一条龙配置: {ConfigName}", configName);
                        return false;
                    }
                }

                if (vm.SelectedConfig == null)
                {
                    return false;
                }

                _ = vm.OnOneKeyExecute();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通过 API 启动一条龙时发生错误");
                return false;
            }
        });

        if (success)
        {
            var displayName = configName ?? "当前配置";
            await WriteJsonResponse(response, 200, new ApiSuccessResponse($"一条龙「{displayName}」已启动"));
        }
        else
        {
            await WriteJsonResponse(response, 400,
                new ApiErrorResponse("启动一条龙失败，请检查配置是否存在"));
        }
    }

    /// <summary>
    /// POST /api/startGroups - 启动配置组
    /// Body: { "groups": ["配置组1", "配置组2"] }
    /// </summary>
    private async Task HandleStartGroups(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadRequestBody(request);
        var groups = body?.GetPropertyOrDefault<List<string>>("groups");

        if (groups == null || groups.Count == 0)
        {
            await WriteJsonResponse(response, 400,
                new ApiErrorResponse("请提供要执行的配置组名称列表，例如: {\"groups\": [\"配置组1\"]}"));
            return;
        }

        // 在 UI 线程上执行
        var result = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var vm = App.GetService<ScriptControlViewModel>();
                if (vm == null)
                {
                    return (false, "无法获取配置组控制器");
                }

                _ = vm.OnStartMultiScriptGroupWithNamesAsync(groups.ToArray());
                return (true, $"配置组 [{string.Join(", ", groups)}] 已启动");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通过 API 启动配置组时发生错误");
                return (false, $"启动配置组失败: {ex.Message}");
            }
        });

        if (result.Item1)
        {
            await WriteJsonResponse(response, 200, new ApiSuccessResponse(result.Item2));
        }
        else
        {
            await WriteJsonResponse(response, 400, new ApiErrorResponse(result.Item2));
        }
    }

    /// <summary>
    /// POST /api/stop - 停止当前任务
    /// </summary>
    private async Task HandleStop(HttpListenerResponse response)
    {
        try
        {
            CancellationContext.Instance.ManualCancel();
            await WriteJsonResponse(response, 200, new ApiSuccessResponse("已发送停止指令"));
        }
        catch (Exception ex)
        {
            await WriteJsonResponse(response, 500, new ApiErrorResponse($"停止任务失败: {ex.Message}"));
        }
    }

    private static async Task<Dictionary<string, object>?> ReadRequestBody(HttpListenerRequest request)
    {
        if (request.InputStream == null || request.ContentLength64 == 0)
        {
            return null;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(body);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteJsonResponse(HttpListenerResponse response, int statusCode, object body)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private record ApiSuccessResponse(bool Success, string Message)
    {
        public ApiSuccessResponse(string message) : this(true, message) { }
    }

    private record ApiErrorResponse(bool Success, string Error)
    {
        public ApiErrorResponse(string error) : this(false, error) { }
    }
}

/// <summary>
/// JSON 辅助扩展方法
/// </summary>
internal static class JsonElementExtensions
{
    public static T? GetPropertyOrDefault<T>(this Dictionary<string, object>? dict, string key)
    {
        if (dict == null || !dict.TryGetValue(key, out var value))
        {
            return default;
        }

        try
        {
            if (value is JsonElement element)
            {
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)element.GetString()!;
                }
                if (typeof(T) == typeof(List<string>))
                {
                    return (T)(object)element.EnumerateArray().Select(x => x.GetString()!).ToList()!;
                }
                return element.Deserialize<T>();
            }
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }
}
