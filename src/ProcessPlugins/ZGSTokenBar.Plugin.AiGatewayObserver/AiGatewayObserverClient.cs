using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Plugin.AiGatewayObserver;

internal enum ObserverFailureKind
{
    None,
    MissingCredentials,
    Authentication,
    Timeout,
    Http,
    Network,
    InvalidResponse,
    Configuration,
    BalanceUnavailable,
}

internal sealed record ObserverFetchResult(
    DeepSeekBalanceSnapshot? Snapshot,
    ObserverFailureKind Failure,
    bool IsCached = false,
    int? HttpStatus = null)
{
    public static ObserverFetchResult Current(DeepSeekBalanceSnapshot snapshot) =>
        new(snapshot, ObserverFailureKind.None);
}

internal sealed record DeepSeekBalanceSnapshot(
    DateTimeOffset ObservedAt,
    bool IsAvailable,
    string Currency,
    decimal TotalBalance,
    decimal ToppedUpBalance,
    decimal GrantedBalance);

internal sealed class AiGatewayObserverClient : IDisposable
{
    internal const int MaximumResponseBytes = 64 * 1024;
    internal const int MaximumLocalDocumentBytes = 1024 * 1024;
    internal const string DefaultCredentialReference = "DEEPSEEK_API_KEY";
    internal const string CacheFileName = "deepseek-harness-balance.v2.json";
    internal const string OfficialBalanceEndpoint = "https://api.deepseek.com/user/balance";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;
    private readonly string? _harnessHome;
    private readonly string? _cachePath;

    public AiGatewayObserverClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
            UseProxy = false,
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        _endpoint = new Uri(OfficialBalanceEndpoint, UriKind.Absolute);
        _ownsClient = true;
        _timeout = TimeSpan.FromSeconds(8);
        _harnessHome = ResolveDefaultHarnessHome();
        _cachePath = ResolveDefaultCachePath();
    }

    internal AiGatewayObserverClient(
        HttpClient httpClient,
        Uri endpoint,
        TimeSpan? timeout = null,
        string? harnessHome = null,
        string? cachePath = null)
    {
        _httpClient = httpClient;
        _endpoint = ValidateEndpoint(endpoint);
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        _harnessHome = harnessHome ?? ResolveDefaultHarnessHome();
        _cachePath = cachePath ?? ResolveDefaultCachePath();
    }

    public async ValueTask<ObserverFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        string? credential;
        try
        {
            credential = ResolveHarnessCredential(_harnessHome);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or DecoderFallbackException)
        {
            return new(null, ObserverFailureKind.Configuration);
        }

        if (credential is null)
        {
            return new(null, ObserverFailureKind.MissingCredentials);
        }
        var credentialFingerprint = CredentialFingerprint(credential);

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return WithCacheFallback(
                    ObserverFailureKind.Authentication,
                    credentialFingerprint,
                    (int)response.StatusCode);
            }
            if (!response.IsSuccessStatusCode)
            {
                return WithCacheFallback(
                    ObserverFailureKind.Http,
                    credentialFingerprint,
                    (int)response.StatusCode);
            }

            var payload = await ReadBoundedAsync(response.Content, deadline.Token)
                .ConfigureAwait(false);
            var snapshot = Parse(payload, DateTimeOffset.UtcNow);
            if (!snapshot.IsAvailable)
            {
                return WithCacheFallback(
                    ObserverFailureKind.BalanceUnavailable,
                    credentialFingerprint);
            }
            TryWriteCache(snapshot, credentialFingerprint);
            return ObserverFetchResult.Current(snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WithCacheFallback(ObserverFailureKind.Timeout, credentialFingerprint);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return WithCacheFallback(ObserverFailureKind.Network, credentialFingerprint);
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidDataException
                or FormatException
                or OverflowException)
        {
            return WithCacheFallback(ObserverFailureKind.InvalidResponse, credentialFingerprint);
        }
    }

    internal static bool HasLocalCredentials(string? harnessHome = null)
    {
        try
        {
            return ResolveHarnessCredential(harnessHome ?? ResolveDefaultHarnessHome()) is not null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or DecoderFallbackException)
        {
            return false;
        }
    }

    internal bool HasConfiguredLocalCredentials() => HasLocalCredentials(_harnessHome);

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    internal static DeepSeekBalanceSnapshot Parse(
        ReadOnlySpan<byte> payload,
        DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(payload.ToArray(), JsonOptions);
        var root = RequireObject(document.RootElement, "root");
        var isAvailable = RequireBoolean(root, "is_available");
        var infos = Require(root, "balance_infos");
        if (infos.ValueKind is not JsonValueKind.Array
            || infos.GetArrayLength() is < 1 or > 16)
        {
            throw new InvalidDataException("balance_infos must be a bounded non-empty array.");
        }

        var balances = new Dictionary<string, DeepSeekBalanceSnapshot>(StringComparer.Ordinal);
        foreach (var value in infos.EnumerateArray())
        {
            var item = RequireObject(value, "balance_info");
            var currency = RequireCurrency(item, "currency");
            var total = RequireDecimal(item, "total_balance");
            var toppedUp = RequireDecimal(item, "topped_up_balance");
            var granted = RequireDecimal(item, "granted_balance");
            if (toppedUp > decimal.MaxValue - granted || total != toppedUp + granted)
            {
                throw new InvalidDataException("Balance totals are inconsistent.");
            }
            if (!balances.TryAdd(
                    currency,
                    new(
                        observedAt.ToUniversalTime(),
                        isAvailable,
                        currency,
                        total,
                        toppedUp,
                        granted)))
            {
                throw new InvalidDataException("Duplicate balance currency.");
            }
        }

        if (balances.TryGetValue("CNY", out var cny)) return cny;
        if (balances.TryGetValue("USD", out var usd)) return usd;
        if (balances.Count == 1) return balances.Values.Single();
        throw new InvalidDataException("No unambiguous preferred balance currency was returned.");
    }

    internal static string? ResolveHarnessCredential(string? harnessHome)
    {
        if (string.IsNullOrWhiteSpace(harnessHome)) return null;

        var settingsPath = Path.Combine(harnessHome, "settings.yaml");
        var reference = DefaultCredentialReference;
        if (File.Exists(settingsPath))
        {
            var configured = ParseSettingsCredentialReference(ReadBoundedFile(settingsPath));
            if (configured is not null) reference = configured;
        }

        var credentialsPath = Path.Combine(harnessHome, ".credentials.yaml");
        if (File.Exists(credentialsPath))
        {
            var managed = ParseCredentialsDocument(ReadBoundedFile(credentialsPath), reference);
            if (managed is not null) return ValidateCredential(managed);
        }

        var userEnvironmentPath = Path.Combine(harnessHome, ".env");
        if (File.Exists(userEnvironmentPath))
        {
            var fallback = ParseDotEnv(ReadBoundedFile(userEnvironmentPath), reference);
            if (fallback is not null) return ValidateCredential(fallback);
        }

        return null;
    }

    internal static string? ParseSettingsCredentialReference(ReadOnlySpan<byte> payload)
    {
        var lines = DecodeLines(payload);
        var sectionStart = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryMappingLine(lines[index], out var indent, out var key, out var rawValue))
            {
                continue;
            }
            if (indent == 0 && key == "llm-deepseek")
            {
                if (sectionStart >= 0)
                {
                    throw new InvalidDataException("Duplicate llm-deepseek settings section.");
                }
                if (!IsEmptyYamlValue(rawValue))
                {
                    throw new InvalidDataException("Inline llm-deepseek settings are unsupported.");
                }
                sectionStart = index;
            }
        }
        if (sectionStart < 0) return null;

        var sectionEnd = lines.Length;
        for (var index = sectionStart + 1; index < lines.Length; index++)
        {
            if (TryMappingLine(lines[index], out var indent, out _, out _)
                && indent == 0)
            {
                sectionEnd = index;
                break;
            }
        }

        var directIndent = int.MaxValue;
        for (var index = sectionStart + 1; index < sectionEnd; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var indent = CountIndent(line);
            if (indent == 0) break;
            directIndent = Math.Min(directIndent, indent);
        }

        string? result = null;
        for (var index = sectionStart + 1; index < sectionEnd; index++)
        {
            if (!TryMappingLine(lines[index], out var indent, out var key, out var rawValue)
                || indent != directIndent
                || key != "apiKeyEnv")
            {
                continue;
            }
            if (result is not null)
            {
                throw new InvalidDataException("Duplicate llm-deepseek apiKeyEnv setting.");
            }
            result = ParseYamlString(rawValue);
            ValidateCredentialReference(result);
        }
        return result;
    }

    internal static string? ParseCredentialsDocument(
        ReadOnlySpan<byte> payload,
        string credentialReference)
    {
        ValidateCredentialReference(credentialReference);
        string? result = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in DecodeLines(payload))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            if (!TryMappingLine(line, out var indent, out var key, out var rawValue)
                || indent != 0)
            {
                throw new InvalidDataException("Credentials document must be a flat mapping.");
            }
            ValidateCredentialReference(key);
            if (!keys.Add(key))
            {
                throw new InvalidDataException("Duplicate credential reference.");
            }
            var value = ParseCredentialYamlString(rawValue);
            if (key == credentialReference) result = value;
        }
        return result;
    }

    internal static string? ParseDotEnv(
        ReadOnlySpan<byte> payload,
        string credentialReference)
    {
        ValidateCredentialReference(credentialReference);
        var keyComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var matched = false;
        string? result = null;
        foreach (var original in DecodeLines(payload))
        {
            var line = original.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            if (!IsCredentialReference(key)
                || !string.Equals(key, credentialReference, keyComparison))
            {
                continue;
            }
            if (matched)
            {
                throw new InvalidDataException("Duplicate credential in user environment file.");
            }
            matched = true;
            result = ParseEnvironmentValue(line[(equals + 1)..]);
        }
        return result;
    }

    internal static byte[] SerializeCache(
        DeepSeekBalanceSnapshot snapshot,
        string credentialFingerprint)
    {
        ValidateCredentialFingerprint(credentialFingerprint);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 2);
            writer.WriteString("source", "deepseek-harness");
            writer.WriteString("provider", "deepseek-official");
            writer.WriteString("credential_fingerprint", credentialFingerprint);
            writer.WriteString("observed_at", snapshot.ObservedAt.ToUniversalTime());
            writer.WriteBoolean("is_available", snapshot.IsAvailable);
            writer.WriteString("currency", snapshot.Currency);
            writer.WriteString(
                "total_balance",
                snapshot.TotalBalance.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "topped_up_balance",
                snapshot.ToppedUpBalance.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(
                "granted_balance",
                snapshot.GrantedBalance.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    internal static DeepSeekBalanceSnapshot ParseCache(
        ReadOnlySpan<byte> payload,
        string expectedCredentialFingerprint)
    {
        ValidateCredentialFingerprint(expectedCredentialFingerprint);
        using var document = JsonDocument.Parse(payload.ToArray(), JsonOptions);
        var root = RequireObject(document.RootElement, "root");
        RequireExactProperties(
            root,
            "schema_version",
            "source",
            "provider",
            "credential_fingerprint",
            "observed_at",
            "is_available",
            "currency",
            "total_balance",
            "topped_up_balance",
            "granted_balance");
        if (RequireInt32(root, "schema_version") != 2
            || RequireString(root, "source") != "deepseek-harness"
            || RequireString(root, "provider") != "deepseek-official")
        {
            throw new InvalidDataException("Cached balance identity is invalid.");
        }
        var actualFingerprint = RequireString(root, "credential_fingerprint");
        ValidateCredentialFingerprint(actualFingerprint);
        if (!FixedFingerprintEquals(actualFingerprint, expectedCredentialFingerprint))
        {
            throw new InvalidDataException("Cached balance belongs to another credential.");
        }
        var observedAt = RequireTimestamp(root, "observed_at");
        var currency = RequireCurrency(root, "currency");
        var total = RequireDecimal(root, "total_balance");
        var toppedUp = RequireDecimal(root, "topped_up_balance");
        var granted = RequireDecimal(root, "granted_balance");
        if (toppedUp > decimal.MaxValue - granted || total != toppedUp + granted)
        {
            throw new InvalidDataException("Cached balance totals are inconsistent.");
        }
        if (!RequireBoolean(root, "is_available"))
        {
            throw new InvalidDataException("Cached balance must be a successful observation.");
        }
        return new(
            observedAt,
            true,
            currency,
            total,
            toppedUp,
            granted);
    }

    private ObserverFetchResult WithCacheFallback(
        ObserverFailureKind failure,
        string credentialFingerprint,
        int? httpStatus = null)
    {
        var cached = TryReadCache(credentialFingerprint);
        return cached is null
            ? new(null, failure, false, httpStatus)
            : new(cached, failure, true, httpStatus);
    }

    private DeepSeekBalanceSnapshot? TryReadCache(string credentialFingerprint)
    {
        if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath)) return null;
        try
        {
            var payload = ReadBoundedFile(_cachePath, MaximumResponseBytes);
            return ParseCache(payload, credentialFingerprint);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or DecoderFallbackException)
        {
            return null;
        }
    }

    private void TryWriteCache(
        DeepSeekBalanceSnapshot snapshot,
        string credentialFingerprint)
    {
        if (string.IsNullOrWhiteSpace(_cachePath)) return;
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_cachePath)}.{Guid.NewGuid():N}.tmp");
            var payload = SerializeCache(snapshot, credentialFingerprint);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _cachePath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A cache failure must never hide a fresh provider response.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string? ResolveDefaultHarnessHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(configured)
            && string.Equals(configured, configured.Trim(), StringComparison.Ordinal)
            && Path.IsPathFullyQualified(configured))
        {
            try
            {
                return Path.GetFullPath(configured);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".dsh");
    }

    private static string? ResolveDefaultCachePath()
    {
        var dataRoot = Environment.GetEnvironmentVariable("ZGSTOKENBAR_PLUGIN_DATA");
        return string.IsNullOrWhiteSpace(dataRoot)
            ? null
            : Path.Combine(dataRoot, CacheFileName);
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath != "/user/balance")
        {
            throw new ArgumentException("DeepSeek balance endpoint is invalid.", nameof(endpoint));
        }
        return endpoint;
    }

    private static byte[] ReadBoundedFile(string path, int limit = MaximumLocalDocumentBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length > limit) throw new InvalidDataException("Local document is too large.");
        using var output = new MemoryStream((int)Math.Min(stream.Length, limit));
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer);
            if (read == 0) break;
            if (output.Length + read > limit)
            {
                throw new InvalidDataException("Local document is too large.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("DeepSeek response is too large.");
        }
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("DeepSeek response is too large.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string[] DecodeLines(ReadOnlySpan<byte> payload)
    {
        var text = StrictUtf8.GetString(payload);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool TryMappingLine(
        string line,
        out int indent,
        out string key,
        out string rawValue)
    {
        indent = CountIndent(line);
        key = string.Empty;
        rawValue = string.Empty;
        var content = line[indent..];
        if (content.Length == 0 || content.StartsWith('#')) return false;
        var colon = FindMappingColon(content);
        if (colon <= 0) return false;
        var rawKey = content[..colon].TrimEnd();
        key = rawKey.Length > 0 && rawKey[0] is '\'' or '"'
            ? ParseYamlString(rawKey)
            : rawKey;
        if (!IsPlainYamlKey(key))
        {
            return false;
        }
        rawValue = content[(colon + 1)..];
        return true;
    }

    private static int FindMappingColon(string content)
    {
        if (content.Length == 0 || content[0] is not ('\'' or '"'))
        {
            return content.IndexOf(':');
        }

        var quote = content[0];
        var index = 1;
        while (index < content.Length)
        {
            if (quote == '"' && content[index] == '\\')
            {
                index += 2;
                continue;
            }
            if (content[index] != quote)
            {
                index++;
                continue;
            }
            if (quote == '\'' && index + 1 < content.Length && content[index + 1] == '\'')
            {
                index += 2;
                continue;
            }
            index++;
            while (index < content.Length && content[index] == ' ') index++;
            return index < content.Length && content[index] == ':' ? index : -1;
        }
        throw new InvalidDataException("Quoted YAML key is not terminated.");
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        if (count < line.Length && line[count] == '\t')
        {
            throw new InvalidDataException("Tabs are unsupported in Harness YAML.");
        }
        return count;
    }

    private static bool IsEmptyYamlValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static string ParseYamlString(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length == 0) throw new InvalidDataException("YAML scalar is empty.");
        if (value[0] == '\'') return ParseSingleQuoted(value, yamlEscaping: true);
        if (value[0] == '"') return ParseDoubleQuoted(value);

        var comment = FindPlainComment(value);
        if (comment >= 0) value = value[..comment].TrimEnd();
        if (value.Length == 0
            || value[0] is '[' or '{' or '&' or '*' or '!' or '|' or '>' or '@' or '`'
            || value is "null" or "Null" or "NULL" or "~")
        {
            throw new InvalidDataException("Unsupported YAML scalar.");
        }
        return value;
    }

    private static string ParseCredentialYamlString(string rawValue)
    {
        var trimmed = rawValue.TrimStart();
        var explicitlyQuoted = trimmed.Length > 0 && trimmed[0] is '\'' or '"';
        var value = ParseYamlString(rawValue);
        if (!explicitlyQuoted && IsImplicitNonStringYamlScalar(value))
        {
            throw new InvalidDataException("Credential values must be YAML strings.");
        }
        return value;
    }

    private static bool IsImplicitNonStringYamlScalar(string value)
    {
        if (value is "~"
            or "null" or "Null" or "NULL"
            or "true" or "True" or "TRUE"
            or "false" or "False" or "FALSE"
            or ".nan" or ".NaN" or ".NAN"
            or ".inf" or ".Inf" or ".INF"
            or "+.inf" or "+.Inf" or "+.INF"
            or "-.inf" or "-.Inf" or "-.INF")
        {
            return true;
        }

        var span = value.AsSpan();
        if (span.Length > 2 && span[0] == '0' && span[1] == 'o')
        {
            return AllDigits(span[2..], static character => character is >= '0' and <= '7');
        }
        if (span.Length > 2 && span[0] == '0' && span[1] == 'x')
        {
            return AllDigits(
                span[2..],
                static character => character is >= '0' and <= '9'
                    || character is >= 'a' and <= 'f'
                    || character is >= 'A' and <= 'F');
        }

        var index = 0;
        if (index < span.Length && span[index] is '+' or '-') index++;
        if (index >= span.Length) return false;

        var integerDigits = ConsumeAsciiDigits(span, ref index);
        if (integerDigits && index == span.Length) return true;

        var hasDecimalPoint = index < span.Length && span[index] == '.';
        if (hasDecimalPoint)
        {
            index++;
            var fractionDigits = ConsumeAsciiDigits(span, ref index);
            if (!integerDigits && !fractionDigits) return false;
        }
        else if (!integerDigits)
        {
            return false;
        }

        var hasExponent = index < span.Length && span[index] is 'e' or 'E';
        if (hasExponent)
        {
            index++;
            if (index < span.Length && span[index] is '+' or '-') index++;
            if (!ConsumeAsciiDigits(span, ref index)) return false;
        }
        return index == span.Length && (hasDecimalPoint || hasExponent);
    }

    private static bool AllDigits(
        ReadOnlySpan<char> value,
        Func<char, bool> isDigit)
    {
        if (value.Length == 0) return false;
        foreach (var character in value)
        {
            if (!isDigit(character)) return false;
        }
        return true;
    }

    private static bool ConsumeAsciiDigits(ReadOnlySpan<char> value, ref int index)
    {
        var sawDigit = false;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            sawDigit = true;
            index++;
        }
        return sawDigit;
    }

    private static string ParseEnvironmentValue(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length == 0) return string.Empty;
        if (value[0] == '\'') return ParseSingleQuoted(value, yamlEscaping: false);
        if (value[0] == '"') return ParseDoubleQuoted(value);
        var comment = FindPlainComment(value);
        return (comment >= 0 ? value[..comment] : value).TrimEnd();
    }

    private static string ParseSingleQuoted(string value, bool yamlEscaping)
    {
        var builder = new StringBuilder();
        var index = 1;
        while (index < value.Length)
        {
            if (value[index] != '\'')
            {
                builder.Append(value[index++]);
                continue;
            }
            if (yamlEscaping && index + 1 < value.Length && value[index + 1] == '\'')
            {
                builder.Append('\'');
                index += 2;
                continue;
            }
            index++;
            RequireOnlyCommentAfter(value, index);
            return builder.ToString();
        }
        throw new InvalidDataException("Quoted scalar is not terminated.");
    }

    private static string ParseDoubleQuoted(string value)
    {
        var builder = new StringBuilder();
        var index = 1;
        while (index < value.Length)
        {
            var current = value[index++];
            if (current == '"')
            {
                RequireOnlyCommentAfter(value, index);
                return builder.ToString();
            }
            if (current != '\\')
            {
                builder.Append(current);
                continue;
            }
            if (index >= value.Length)
            {
                throw new InvalidDataException("Quoted scalar escape is incomplete.");
            }
            current = value[index++];
            builder.Append(current switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => throw new InvalidDataException("Quoted scalar escape is unsupported."),
            });
        }
        throw new InvalidDataException("Quoted scalar is not terminated.");
    }

    private static void RequireOnlyCommentAfter(string value, int index)
    {
        var trailing = value[index..].TrimStart();
        if (trailing.Length > 0 && !trailing.StartsWith('#'))
        {
            throw new InvalidDataException("Unexpected content after quoted scalar.");
        }
    }

    private static int FindPlainComment(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
            {
                return index;
            }
        }
        return -1;
    }

    private static string ValidateCredential(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 4096
            || normalized.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidDataException("Harness credential has an invalid format.");
        }
        return normalized;
    }

    internal static string CredentialFingerprint(string credential)
    {
        var normalized = ValidateCredential(credential);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void ValidateCredentialFingerprint(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Credential fingerprint is invalid.");
        }
    }

    private static bool FixedFingerprintEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void ValidateCredentialReference(string value)
    {
        if (!IsCredentialReference(value))
        {
            throw new InvalidDataException("Harness credential reference is invalid.");
        }
    }

    private static bool IsCredentialReference(string value)
    {
        if (value.Length is < 1 or > 128
            || !(value[0] is '_' || value[0] is >= 'A' and <= 'Z' || value[0] is >= 'a' and <= 'z'))
        {
            return false;
        }
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!(character is '_'
                    || character is >= 'A' and <= 'Z'
                    || character is >= 'a' and <= 'z'
                    || character is >= '0' and <= '9'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPlainYamlKey(string value)
    {
        if (value.Length is < 1 or > 128
            || !(value[0] is '_'
                || value[0] is >= 'A' and <= 'Z'
                || value[0] is >= 'a' and <= 'z'))
        {
            return false;
        }
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!(character is '_' or '-'
                    || character is >= 'A' and <= 'Z'
                    || character is >= 'a' and <= 'z'
                    || character is >= '0' and <= '9'))
            {
                return false;
            }
        }
        return true;
    }

    private static JsonElement Require(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"Missing {name}.");
        }
        return value;
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException($"{name} must be an object.");
        }
        return value;
    }

    private static void RequireExactProperties(JsonElement value, params string[] expected)
    {
        var names = new HashSet<string>(expected, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (!names.Remove(property.Name))
            {
                throw new InvalidDataException("Cached balance contains an unknown or duplicate field.");
            }
        }
        if (count != expected.Length || names.Count != 0)
        {
            throw new InvalidDataException("Cached balance fields are incomplete.");
        }
    }

    private static string RequireString(JsonElement parent, string name)
    {
        var value = Require(parent, name);
        if (value.ValueKind is not JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new InvalidDataException($"{name} must be a string.");
        }
        return text;
    }

    private static bool RequireBoolean(JsonElement parent, string name)
    {
        var value = Require(parent, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{name} must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static int RequireInt32(JsonElement parent, string name)
    {
        var value = Require(parent, name);
        if (value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"{name} must be an integer.");
        }
        return result;
    }

    private static string RequireCurrency(JsonElement parent, string name)
    {
        var currency = RequireString(parent, name);
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new InvalidDataException($"{name} must be an ISO-style currency code.");
        }
        return currency;
    }

    private static decimal RequireDecimal(JsonElement parent, string name)
    {
        var value = RequireString(parent, name);
        if (value.Length is < 1 or > 64
            || !decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var result)
            || result < 0)
        {
            throw new InvalidDataException($"{name} must be a non-negative decimal string.");
        }
        return result;
    }

    private static DateTimeOffset RequireTimestamp(JsonElement parent, string name)
    {
        var value = RequireString(parent, name);
        if (!(value.EndsWith('Z')
                || value.Length >= 6 && (value[^6] is '+' or '-') && value[^3] == ':')
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new InvalidDataException($"{name} must be an ISO-8601 timestamp.");
        }
        return timestamp.ToUniversalTime();
    }
}
