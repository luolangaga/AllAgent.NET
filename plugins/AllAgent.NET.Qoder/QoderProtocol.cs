using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Qoder;

/// <summary>Qoder device login URLs and the COSY request envelope used by its inference gateway.</summary>
internal static class QoderProtocol
{
    internal const string ClientId = "e883ade2-e6e3-4d6d-adf7-f92ceff5fdcb";
    internal const string LoginUrl = "https://qoder.com/device/selectAccounts";
    internal const string OpenApiBase = "https://openapi.qoder.sh";
    internal const string DeviceTokenUrl = OpenApiBase + "/api/v1/deviceToken/poll";
    internal const string UserInfoUrl = OpenApiBase + "/api/v1/userinfo";
    internal const string UserStatusUrl = OpenApiBase + "/api/v3/user/status";
    internal const string QuotaUrl = OpenApiBase + "/api/v2/quota/usage";
    internal const string PlanUrl = OpenApiBase + "/api/v2/user/plan";
    internal const string ActivityEligibilityUrl = OpenApiBase + "/api/v2/activity/claim/eligibility";
    internal const string ActivityClaimUrl = OpenApiBase + "/api/v2/activity/claim";
    internal const string RefreshUrl = "https://center.qoder.sh/algo/api/v3/user/refresh_token";
    internal const string ModelListUrl = "https://api3.qoder.sh/algo/api/v2/model/list";
    internal const string ChatUrl = "https://api3.qoder.sh/algo/api/v2/service/pro/sse/agent_chat_generation?FetchKeys=llm_model_result&AgentId=agent_common&Encode=1";
    internal const string ChatSignaturePath = "/api/v2/service/pro/sse/agent_chat_generation";
    internal const string IdeVersion = "1.0.0";
    internal const string ClientType = "5";
    internal const string LoginVersion = "v2";
    internal const string MachineOs = "x86_64_windows";
    internal const string MachineType = "5";
    internal const string ServerPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDA8iMH5c02LilrsERw9t6Pv5Nc
        4k6Pz1EaDicBMpdpxKduSZu5OANqUq8er4GM95omAGIOPOh+Nx0spthYA2BqGz+l
        6HRkPJ7S236FZz73In/KVuLnwI8JJ2CbuJap8kvheCCZpmAWpb/cPx/3Vr/J6I17
        XcW+ML9FoCI6AOvOzwIDAQAB
        -----END PUBLIC KEY-----
        """;
    private const string StandardAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string CustomAlphabet = "_doRTgHZBKcGVjlvpC,@aFSx#DPuNJme&i*MzLOEn)sUrthbf%Y^w.(kIQyXqWA!";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string NewUuid() => Guid.NewGuid().ToString("D");
    internal static string NewSimpleUuid() => Guid.NewGuid().ToString("N");
    internal static string NewMachineId() => Guid.NewGuid().ToString("D");
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>PKCE S256 pair; the verifier is a 32-byte URL-safe random string.</summary>
    internal static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    internal static Uri BuildVerificationUrl(string nonce, string challenge, string machineId)
    {
        var query = new StringBuilder(LoginUrl)
            .Append("?nonce=").Append(Uri.EscapeDataString(nonce))
            .Append("&challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&challenge_method=S256")
            .Append("&client_id=").Append(Uri.EscapeDataString(ClientId));
        if (!string.IsNullOrWhiteSpace(machineId))
            query.Append("&machine_id=").Append(Uri.EscapeDataString(machineId));
        return new Uri(query.ToString(), UriKind.Absolute);
    }

    internal static Uri DevicePollUrl(string nonce, string verifier) =>
        new(DeviceTokenUrl + "?nonce=" + Uri.EscapeDataString(nonce) + "&verifier=" + Uri.EscapeDataString(verifier) + "&challenge_method=S256", UriKind.Absolute);

    /// <summary>Moves the trailing third of a standard Base64 string to the front and swaps alphabets.</summary>
    internal static string EncodeBody(ReadOnlySpan<byte> plaintext)
    {
        var standard = Convert.ToBase64String(plaintext);
        var length = standard.Length;
        var third = length / 3;
        var rearranged = string.Concat(standard.AsSpan(length - third), standard.AsSpan(third, length - 2 * third), standard.AsSpan(0, third));
        var output = new char[length];
        for (var index = 0; index < length; index++)
        {
            var character = rearranged[index];
            if (character == '=') output[index] = '$';
            else
            {
                var position = StandardAlphabet.IndexOf(character);
                output[index] = position >= 0 ? CustomAlphabet[position] : character;
            }
        }
        return new string(output);
    }

    /// <summary>Inverse of <see cref="EncodeBody"/>; used by tests and diagnostics.</summary>
    internal static byte[] DecodeBody(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return [];
        var standard = new char[encoded.Length];
        for (var index = 0; index < encoded.Length; index++)
        {
            var character = encoded[index];
            if (character == '$') standard[index] = '=';
            else
            {
                var position = CustomAlphabet.IndexOf(character);
                standard[index] = position >= 0 ? StandardAlphabet[position] : character;
            }
        }
        var text = new string(standard);
        var third = text.Length / 3;
        var restored = string.Concat(text.AsSpan(text.Length - third), text.AsSpan(third, text.Length - 2 * third), text.AsSpan(0, third));
        return Convert.FromBase64String(restored);
    }

    internal static string Signature(string payloadB64, string cosyKey, string cosyDate, string bodyLatin1, string signaturePath) =>
        Convert.ToHexString(MD5.HashData(Encoding.Latin1.GetBytes(payloadB64 + "\n" + cosyKey + "\n" + cosyDate + "\n" + bodyLatin1 + "\n" + signaturePath))).ToLowerInvariant();

    internal static string SignaturePath(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.StartsWith("/algo/", StringComparison.Ordinal) ? path[5..] : path;
    }

    internal static Dictionary<string, string> BuildCosyHeaders(byte[] body, Uri url, QoderSession session)
    {
        var accessToken = RequireHeaderValue(session.AccessToken, "Qoder 访问令牌");
        var identity = session.Identity;
        var userId = RequireHeaderValue(identity.UserId, "Qoder 用户 ID");
        var machineId = string.IsNullOrWhiteSpace(identity.MachineId) ? NewMachineId() : RequireHeaderValue(identity.MachineId, "Qoder 机器 ID");
        var machineToken = string.IsNullOrWhiteSpace(identity.MachineToken) ? machineId : RequireHeaderValue(identity.MachineToken, "Qoder 机器令牌");
        var machineType = string.IsNullOrWhiteSpace(identity.MachineType) ? MachineType : RequireHeaderValue(identity.MachineType, "Qoder 机器类型");
        if (identity.OrganizationId.Any(char.IsControl)) throw new InvalidOperationException("Qoder 组织 ID 无效，请重新登录。");

        var aesKey = NewUuid()[..16];
        var info = EncryptInfo(identity, accessToken, aesKey);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(ServerPublicKey);
        var cosyKey = Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(aesKey), RSAEncryptionPadding.Pkcs1));
        var payload = new JsonObject
        {
            ["version"] = "v1",
            ["requestId"] = NewUuid(),
            ["info"] = info,
            ["cosyVersion"] = IdeVersion,
            ["ideVersion"] = ""
        };
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions)));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signaturePath = SignaturePath(url);
        var bodyText = Encoding.Latin1.GetString(body);
        var signature = Signature(payloadB64, cosyKey, timestamp, bodyText, signaturePath);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer COSY." + payloadB64 + "." + signature,
            ["Cosy-Key"] = cosyKey,
            ["Cosy-User"] = userId,
            ["Cosy-Date"] = timestamp,
            ["Cosy-Version"] = IdeVersion,
            ["Cosy-Machineid"] = machineId,
            ["Cosy-Machinetoken"] = machineToken,
            ["Cosy-Machinetype"] = machineType,
            ["Cosy-Machineos"] = MachineOs,
            ["Cosy-Clienttype"] = ClientType,
            ["Cosy-Clientip"] = "127.0.0.1",
            ["Cosy-Bodyhash"] = Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant(),
            ["Cosy-Bodylength"] = body.Length.ToString(CultureInfo.InvariantCulture),
            ["Cosy-Sigpath"] = signaturePath,
            ["Cosy-Data-Policy"] = "disagree",
            ["Cosy-Organization-Id"] = identity.OrganizationId,
            ["Cosy-Organization-Tags"] = "",
            ["Login-Version"] = LoginVersion,
            ["X-Request-Id"] = NewUuid()
        };
    }

    private static string EncryptInfo(QoderIdentity identity, string accessToken, string aesKey)
    {
        var payload = new JsonObject
        {
            ["uid"] = identity.UserId,
            ["security_oauth_token"] = accessToken,
            ["name"] = identity.Name,
            ["aid"] = "",
            ["email"] = identity.Email
        };
        var plaintext = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions));
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(aesKey);
        aes.IV = aes.Key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
    }

    /// <summary>Builds the chat payload Qoder's agent gateway expects.</summary>
    internal static JsonObject BuildChatPayload(ChatRequest request, JsonElement modelConfig, QoderSession session)
    {
        var key = NormalizeModel(request.Config.DefaultModel);
        var messages = BuildMessages(request, out var system, out var lastUser);
        var tools = BuildTools(request.Tools);
        var configNode = CloneModelConfig(modelConfig, key);
        var effort = ReasoningEffort(request.Config);
        var isReasoning = modelConfig.ValueKind == JsonValueKind.Object && ReadBool(modelConfig, "is_reasoning") == true || effort != null;
        var modelMax = modelConfig.ValueKind == JsonValueKind.Object ? ReadInt(modelConfig, "max_output_tokens") ?? 0 : 0;
        var maxTokens = modelMax > 0 ? modelMax : 32768;
        if (request.Config.MaxTokens > 0) maxTokens = Math.Min(maxTokens, request.Config.MaxTokens);
        if (effort != null) maxTokens = Math.Max(maxTokens, EffortTokens(effort));
        maxTokens = Math.Clamp(maxTokens, 1024, 131072);

        var messagesJson = messages.ToJsonString(JsonOptions);
        var toolsJson = tools.ToJsonString(JsonOptions);
        var sessionId = Hash16("qoder-session", session.Identity.UserId, key);
        var recordId = Hash16("qoder-record", key, messagesJson, toolsJson, maxTokens.ToString(CultureInfo.InvariantCulture));
        var parameters = new JsonObject { ["max_tokens"] = maxTokens };
        if (effort != null) parameters["reasoning_effort"] = effort;

        return new JsonObject
        {
            ["request_id"] = NewUuid(),
            ["request_set_id"] = recordId,
            ["chat_record_id"] = recordId,
            ["session_id"] = sessionId,
            ["stream"] = true,
            ["chat_task"] = "FREE_INPUT",
            ["is_reply"] = true,
            ["is_retry"] = false,
            ["source"] = 1,
            ["version"] = "3",
            ["session_type"] = "qodercli",
            ["agent_id"] = "agent_common",
            ["task_id"] = "common",
            ["code_language"] = "",
            ["chat_prompt"] = "",
            ["image_urls"] = null,
            ["aliyun_user_type"] = string.IsNullOrWhiteSpace(session.Identity.UserType) ? "personal_standard" : session.Identity.UserType,
            ["system"] = system,
            ["messages"] = messages,
            ["tools"] = tools,
            ["parameters"] = parameters,
            ["chat_context"] = new JsonObject
            {
                ["chatPrompt"] = "",
                ["imageUrls"] = null,
                ["extra"] = new JsonObject
                {
                    ["context"] = new JsonArray(),
                    ["modelConfig"] = new JsonObject { ["key"] = key, ["is_reasoning"] = isReasoning },
                    ["originalContent"] = lastUser
                },
                ["features"] = new JsonArray(),
                ["text"] = lastUser
            },
            ["model_config"] = configNode,
            ["business"] = new JsonObject
            {
                ["product"] = "cli",
                ["version"] = "1.0.0",
                ["type"] = "agent",
                ["stage"] = "start",
                ["id"] = NewUuid(),
                ["name"] = FirstCharacters(lastUser, 30),
                ["begin_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
    }

    private static JsonArray BuildMessages(ChatRequest request, out string system, out string lastUser)
    {
        var messages = new JsonArray();
        var systemParts = new List<string>();
        lastUser = "";
        foreach (var message in request.Messages)
        {
            if (message.Role is "system" or "developer")
            {
                if (!string.IsNullOrWhiteSpace(message.Content)) systemParts.Add(message.Content);
                continue;
            }
            if (message.Role == "assistant")
            {
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = message.Content ?? "" };
                var hasCalls = false;
                if (!string.IsNullOrWhiteSpace(message.ToolCallsJson))
                {
                    try
                    {
                        if (JsonNode.Parse(message.ToolCallsJson) is JsonArray calls && calls.Count > 0)
                        {
                            assistant["tool_calls"] = calls.DeepClone();
                            hasCalls = true;
                        }
                    }
                    catch (JsonException) { }
                }
                if (!string.IsNullOrEmpty(message.ReasoningContent)) assistant["reasoning_content"] = message.ReasoningContent;
                if (string.IsNullOrWhiteSpace(message.Content) && !hasCalls) continue;
                messages.Add(assistant);
                continue;
            }
            if (message.Role == "tool")
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = message.ToolCallId ?? "",
                    ["content"] = message.Content ?? ""
                });
                continue;
            }
            if (message.Images.Count > 0)
            {
                ImageAttachment.Validate(message.Images);
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(message.Content)) parts.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                foreach (var image in message.Images)
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image.DataUrl } });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                lastUser = message.Content ?? "";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(message.Content)) continue;
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Content });
                lastUser = message.Content;
            }
        }
        system = string.Join("\n\n", systemParts);
        return messages;
    }

    private static JsonArray BuildTools(IReadOnlyList<SkillDefinition>? definitions)
    {
        var tools = new JsonArray();
        foreach (var tool in (definitions ?? []).OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            JsonNode? parameters = tool.Parameters switch
            {
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                JsonDocument document => JsonNode.Parse(document.RootElement.GetRawText()),
                null => new JsonObject(),
                _ => JsonSerializer.SerializeToNode(tool.Parameters)
            };
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters is JsonObject or JsonArray ? parameters : new JsonObject()
                }
            });
        }
        return tools;
    }

    private static JsonObject CloneModelConfig(JsonElement config, string key)
    {
        if (config.ValueKind == JsonValueKind.Object)
        {
            try
            {
                if (JsonNode.Parse(config.GetRawText()) is JsonObject clone)
                {
                    clone["key"] = key;
                    return clone;
                }
            }
            catch (JsonException) { }
        }
        return new JsonObject
        {
            ["key"] = key,
            ["display_name"] = key,
            ["model"] = "",
            ["format"] = "openai",
            ["is_vl"] = false,
            ["is_reasoning"] = false,
            ["api_key"] = "",
            ["url"] = "",
            ["source"] = "system",
            ["max_input_tokens"] = 180000,
            ["max_output_tokens"] = 32768
        };
    }

    internal static string NormalizeModel(string? model)
    {
        var value = (model ?? "").Trim();
        if (value.StartsWith("qoder/", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        return string.IsNullOrWhiteSpace(value) ? "auto" : value;
    }

    internal static string? ReasoningEffort(ApiConfig config)
    {
        var effort = (config.ReasoningEffort ?? "").Trim().ToLowerInvariant();
        return effort switch
        {
            "default" or "" => config.ThinkingEnabled ? "medium" : null,
            "none" => null,
            "thinking" => "medium",
            "minimal" => "low",
            "xhigh" or "ultra" => "max",
            "budget" => config.ThinkingBudget switch
            {
                <= 0 => "medium",
                <= 4000 => "low",
                <= 12000 => "medium",
                <= 24000 => "high",
                _ => "max"
            },
            "low" or "medium" or "high" or "max" => effort,
            _ => config.ThinkingEnabled ? "medium" : null
        };
    }

    private static int EffortTokens(string effort) => effort switch
    {
        "low" => 8192,
        "medium" => 16384,
        "high" => 24576,
        _ => 32768
    };

    private static string Hash16(string prefix, params string[] parts)
    {
        var text = prefix + "\0" + string.Join("\0", parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    private static string FirstCharacters(string value, int count)
    {
        if (value.Length <= count) return value;
        var length = char.IsHighSurrogate(value[count - 1]) ? count - 1 : count;
        return value[..length];
    }

    private static string RequireHeaderValue(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(label + " 无效，请重新登录 Qoder。");
        return value;
    }

    internal static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    internal static int? ReadInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    internal static long? ReadLong(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    internal static bool? ReadBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result)) return result;
        }
        return null;
    }

    internal static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    /// <summary>Qoder returns RFC3339 timestamps or millisecond/second durations depending on the flow.</summary>
    internal static DateTimeOffset ReadExpiry(JsonElement root, string atName, string inName, DateTimeOffset fallback)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(atName, out var at) && ParseTimestamp(at) is { } parsed) return parsed;
            if (root.TryGetProperty(inName, out var duration))
            {
                var value = duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var number) ? number
                    : duration.ValueKind == JsonValueKind.String && long.TryParse(duration.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
                if (value > 0)
                {
                    // Device tokens report ~30 days as 2_591_999_998; values that large are milliseconds.
                    return value > 31_536_000 ? DateTimeOffset.UtcNow.AddMilliseconds(value) : DateTimeOffset.UtcNow.AddSeconds(value);
                }
            }
        }
        return fallback;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            // Numeric strings are Unix timestamps, not years (2026 would otherwise parse as a date).
            if (text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return FromUnix(number);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric)) return FromUnix(numeric);
        return null;
    }

    private static DateTimeOffset FromUnix(long value) => value > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
}

internal sealed class QoderIdentity
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string OrganizationName { get; set; } = "";
    public string UserType { get; set; } = "personal_standard";
    public string YxUid { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string MachineToken { get; set; } = "";
    public string MachineType { get; set; } = QoderProtocol.MachineType;
}

internal sealed record QoderSession(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, QoderIdentity Identity);
