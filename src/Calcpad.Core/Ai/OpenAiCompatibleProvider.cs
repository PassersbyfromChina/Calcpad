using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>One message of a chat request. <see cref="Png"/> is attached only when the user approved it.</summary>
public sealed record AiChatMessage(string Role, string Content, byte[]? Png = null);

/// <summary>A chat transport. Implemented by the OpenAI compatible provider; test doubles implement it too.</summary>
public interface IAiChatProvider
{
    /// <summary>Provider id, e.g. <c>openai</c>.</summary>
    string Id { get; }

    /// <summary>What the registry advertises.</summary>
    AiProviderInfo Info { get; }

    /// <summary>Sends the messages. Never throws for network problems: the reason is returned instead.</summary>
    ValueTask<AiAnswer> ChatAsync(AiProviderSettings settings, IReadOnlyList<AiChatMessage> messages, IProgress<string>? streamed = null, CancellationToken ct = default);

    /// <summary>Sends a one word probe. Must not carry any canvas content.</summary>
    ValueTask<(bool Ok, string Message)> TestAsync(AiProviderSettings settings, CancellationToken ct = default);
}

/// <summary>Options of the OpenAI compatible transport.</summary>
public sealed record OpenAiCompatibleOptions
{
    /// <summary>Append <c>/v1</c> when the base URL carries no path of its own (true for OpenAI, Ollama, LM Studio).</summary>
    public bool UseV1PrefixByDefault { get; init; } = true;

    /// <summary>Request timeout. Applied per request, never retried in the background.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum characters of an error body echoed back to the user.</summary>
    public int MaxErrorBodyLength { get; init; } = 400;
}

/// <summary>
/// OpenAI compatible chat transport: <c>POST {BaseUrl}/chat/completions</c> with tolerant URL joining,
/// bearer auth and SSE streaming. It sends exactly the messages it is given — the egress guard has
/// already shown that payload to the user, so nothing else may be added in transit.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiChatProvider
{
    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _options;

    /// <summary>Creates the transport. The caller owns <paramref name="http"/>, which allows a fake handler in tests.</summary>
    public OpenAiCompatibleProvider(
        string id,
        string displayName,
        bool supportsVision,
        bool isLocal,
        HttpClient? http = null,
        OpenAiCompatibleOptions? options = null)
    {
        Id = id;
        Info = new AiProviderInfo(id, displayName, supportsVision, isLocal, null);
        _http = http ?? new HttpClient();
        _options = options ?? new OpenAiCompatibleOptions();
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public AiProviderInfo Info { get; }

    /// <summary>
    /// Joins a user supplied base URL with the chat completions path. Accepts a full endpoint, a
    /// <c>/v1</c> root and a bare host, which is what real users paste into the settings box.
    /// </summary>
    public static string ResolveEndpoint(string baseUrl, bool useV1PrefixByDefault = true)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is empty.", nameof(baseUrl));
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/chat/completions";
        }

        var hasPath = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.AbsolutePath.Trim('/').Length > 0;
        if (hasPath || !useV1PrefixByDefault)
        {
            return trimmed + "/chat/completions";
        }

        return trimmed + "/v1/chat/completions";
    }

    /// <inheritdoc />
    public async ValueTask<AiAnswer> ChatAsync(
        AiProviderSettings settings,
        IReadOnlyList<AiChatMessage> messages,
        IProgress<string>? streamed = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);
        var usedCloud = !Info.IsLocal;

        if (!settings.IsUsable)
        {
            return Failure(settings, usedCloud, "供应商未启用或缺少 BaseUrl");
        }

        string endpoint;
        try
        {
            endpoint = ResolveEndpoint(settings.BaseUrl!, _options.UseV1PrefixByDefault);
        }
        catch (Exception ex)
        {
            return Failure(settings, usedCloud, $"BaseUrl 无效：{ex.Message}");
        }

        var body = BuildBody(settings, messages, stream: true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
                return Failure(settings, usedCloud, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}：{Truncate(errorBody)}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadStreamAsync(settings, response, usedCloud, streamed, timeout.Token).ConfigureAwait(false);
            }

            var payload = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            return ParseSingleResponse(settings, payload, usedCloud);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(settings, usedCloud, $"请求超时（{_options.Timeout.TotalSeconds:0} 秒）");
        }
        catch (OperationCanceledException)
        {
            return Failure(settings, usedCloud, "请求已取消");
        }
        catch (HttpRequestException ex)
        {
            return Failure(settings, usedCloud, $"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            return Failure(settings, usedCloud, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<(bool Ok, string Message)> TestAsync(AiProviderSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsUsable)
        {
            return (false, "供应商未启用或缺少 BaseUrl");
        }

        // A single word probe: no canvas content, no system prompt, nothing else.
        var answer = await ChatAsync(
            settings,
            [new AiChatMessage("user", "ping")],
            streamed: null,
            ct).ConfigureAwait(false);

        if (answer.Text.Length > 0)
        {
            return (true, $"连接成功，模型回复：{Truncate(answer.Text, 120)}");
        }

        return (false, answer.Reason ?? "连接失败");
    }

    private string BuildBody(AiProviderSettings settings, IReadOnlyList<AiChatMessage> messages, bool stream)
    {
        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"model\":").Append(JsonSerializer.Serialize(settings.Model ?? "gpt-4o-mini")).Append(',');
        builder.Append("\"stream\":").Append(stream ? "true" : "false").Append(',');
        builder.Append("\"messages\":[");
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('{');
            builder.Append("\"role\":").Append(JsonSerializer.Serialize(message.Role)).Append(',');
            if (message.Png is { Length: > 0 })
            {
                builder.Append("\"content\":[");
                builder.Append("{\"type\":\"text\",\"text\":").Append(JsonSerializer.Serialize(message.Content)).Append("},");
                builder.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":")
                    .Append(JsonSerializer.Serialize($"data:image/png;base64,{Convert.ToBase64String(message.Png)}"))
                    .Append("}}]");
            }
            else
            {
                builder.Append("\"content\":").Append(JsonSerializer.Serialize(message.Content));
            }

            builder.Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private async ValueTask<AiAnswer> ReadStreamAsync(
        AiProviderSettings settings,
        HttpResponseMessage response,
        bool usedCloud,
        IProgress<string>? streamed,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var interrupted = false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    interrupted = text.Length > 0;
                    break;
                }

                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                var delta = ExtractDelta(payload);
                if (!string.IsNullOrEmpty(delta))
                {
                    text.Append(delta);
                    streamed?.Report(delta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            interrupted = text.Length > 0;
            if (!interrupted)
            {
                return Failure(settings, usedCloud, $"流式响应中断：{ex.Message}");
            }

            return new AiAnswer(
                text.ToString(),
                Info.DisplayName,
                settings.Model,
                usedCloud,
                false,
                $"流式响应中断（已收到部分内容）：{ex.Message}");
        }

        if (text.Length == 0)
        {
            return Failure(settings, usedCloud, "服务返回了空的流式响应");
        }

        return new AiAnswer(
            text.ToString(),
            Info.DisplayName,
            settings.Model,
            usedCloud,
            false,
            interrupted ? "流式响应未以 [DONE] 结束（已收到部分内容）" : null);
    }

    private AiAnswer ParseSingleResponse(AiProviderSettings settings, string payload, bool usedCloud)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var text = ExtractMessageContent(document.RootElement);
            if (string.IsNullOrEmpty(text))
            {
                return Failure(settings, usedCloud, "服务返回了空的响应");
            }

            return new AiAnswer(text, Info.DisplayName, settings.Model, usedCloud);
        }
        catch (JsonException ex)
        {
            return Failure(settings, usedCloud, $"响应不是合法 JSON：{ex.Message}");
        }
    }

    private static string? ExtractMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(partText.GetString());
                    }
                }

                return builder.ToString();
            }
        }

        return first.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String ? legacy.GetString() : null;
    }

    private static string? ExtractDelta(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
            {
                return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            }

            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var full))
            {
                return full.ValueKind == JsonValueKind.String ? full.GetString() : null;
            }

            return first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private AiAnswer Failure(AiProviderSettings settings, bool usedCloud, string reason) =>
        new(string.Empty, Info.DisplayName, settings.Model, usedCloud, false, reason);

    private string Truncate(string value) => Truncate(value, _options.MaxErrorBodyLength);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    /// <summary>Builds the egress preview part for an image, used by the AI service before upload.</summary>
    internal static string DescribeImage(byte[] png, CRect region) =>
        $"PNG {png.Length} 字节，区域 {region}";
}
