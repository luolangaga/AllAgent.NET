using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace AllAgent.NET.Core.Bridge;

/// <summary>Where the browser workspace listens and how remote clients prove access.</summary>
public sealed record WebUiNetworkOptions(bool? Lan = null, int? Port = null, string? Password = null, bool ClearPassword = false);

/// <summary>The address, access and pairing information the terminal and the settings page show.</summary>
public sealed record WebUiNetworkInfo(
    bool Lan,
    int Port,
    string Url,
    string LocalUrl,
    IReadOnlyList<string> Urls,
    bool AuthenticationRequired,
    bool PasswordConfigured,
    string? AccessUrl,
    string? QrDataUrl);

/// <summary>Persisted host-wide network settings. Passwords are stored as a salted PBKDF2 hash.</summary>
public sealed class WebUiNetworkSettings
{
    private const string FileName = "web-ui-access.json";

    public bool Lan { get; set; }
    public int Port { get; set; }
    public string? PasswordSalt { get; set; }
    public string? PasswordHash { get; set; }

    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    public static WebUiNetworkSettings Load(string directory)
    {
        try
        {
            var path = PathFor(directory);
            if (!File.Exists(path)) return new WebUiNetworkSettings();
            return JsonSerializer.Deserialize<WebUiNetworkSettings>(File.ReadAllText(path), JsonOptions.Default) ?? new WebUiNetworkSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoopbackHttp.Log("[WebUiNetwork] settings unreadable; using defaults: " + ex.Message);
            return new WebUiNetworkSettings();
        }
    }

    public void Save(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions.Default));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoopbackHttp.Log("[WebUiNetwork] settings could not be saved: " + ex.Message);
        }
    }
}

/// <summary>
/// Access control for the browser workspace. A per-run token powers QR pairing; an optional
/// password gates manual access to a non-loopback listener.
/// </summary>
public sealed class WebUiAccessControl
{
    private const int Iterations = 100_000;
    private readonly object _sync = new();
    private byte[]? _salt;
    private byte[]? _hash;

    public WebUiAccessControl(WebUiNetworkSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.PasswordSalt) && !string.IsNullOrEmpty(settings.PasswordHash))
        {
            try
            {
                _salt = Convert.FromBase64String(settings.PasswordSalt);
                _hash = Convert.FromBase64String(settings.PasswordHash);
            }
            catch (FormatException)
            {
                _salt = null;
                _hash = null;
            }
        }
    }

    /// <summary>Short-lived secret embedded in the QR code; valid until the host stops.</summary>
    public string AccessToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public bool PasswordConfigured
    {
        get { lock (_sync) return _hash != null && _salt != null; }
    }

    public bool VerifyToken(string? token) => !string.IsNullOrEmpty(token) && FixedEquals(token, AccessToken);

    public bool VerifyPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        byte[] salt, expected;
        lock (_sync)
        {
            if (_salt == null || _hash == null) return false;
            salt = _salt;
            expected = _hash;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void SetPassword(string? password)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(password))
            {
                _salt = null;
                _hash = null;
                return;
            }
            _salt = RandomNumberGenerator.GetBytes(16);
            _hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), _salt, Iterations, HashAlgorithmName.SHA256, 32);
        }
    }

    public void CopyCredentialsTo(WebUiNetworkSettings settings)
    {
        lock (_sync)
        {
            settings.PasswordSalt = _salt == null ? null : Convert.ToBase64String(_salt);
            settings.PasswordHash = _hash == null ? null : Convert.ToBase64String(_hash);
        }
    }

    /// <summary>A readable password for a LAN listener that has none configured yet.</summary>
    public static string GeneratePassword(int length = 10)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var characters = new char[length];
        for (var i = 0; i < characters.Length; i++) characters[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(characters);
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>Local addresses, Host-header validation and the pairing QR code.</summary>
public static class WebUiAddress
{
    private static readonly Lazy<string[]> _machineNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Dns.GetHostName() };
        try
        {
            var entry = Dns.GetHostEntry(Dns.GetHostName());
            if (!string.IsNullOrWhiteSpace(entry.HostName)) names.Add(entry.HostName);
        }
        catch (SocketException) { /* offline machines still have a NetBIOS name */ }
        return names.ToArray();
    });

    private sealed record LocalAddress(IPAddress Address, bool HasGateway, NetworkInterfaceType Type);

    private static readonly Lazy<LocalAddress[]> _localAddresses = new(() =>
    {
        var addresses = new List<LocalAddress>
        {
            new(IPAddress.Loopback, false, NetworkInterfaceType.Loopback),
            new(IPAddress.IPv6Loopback, false, NetworkInterfaceType.Loopback),
        };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = network.GetIPProperties();
                var gateway = properties.GatewayAddresses.Any(item => item.Address is { } address
                    && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any));
                foreach (var info in properties.UnicastAddresses)
                {
                    var address = info.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address)
                        && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        addresses.Add(new LocalAddress(address, gateway, network.NetworkInterfaceType));
                    else if (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !IPAddress.IsLoopback(address))
                        addresses.Add(new LocalAddress(address, gateway, network.NetworkInterfaceType));
                }
            }
        }
        catch (NetworkInformationException) { /* keep loopback */ }
        return addresses.DistinctBy(item => item.Address).ToArray();
    });

    /// <summary>LAN IPv4 addresses, the default-gateway interface first so the QR points somewhere reachable.</summary>
    public static IReadOnlyList<IPAddress> LanAddresses() => _localAddresses.Value
        .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address))
        .OrderByDescending(item => item.HasGateway)
        .ThenByDescending(item => item.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .ThenByDescending(item => IsPrivate(item.Address))
        .Select(item => item.Address)
        .ToArray();

    public static IReadOnlyList<string> Urls(int port) => LanAddresses().Select(address => $"http://{address}:{port}/").ToArray();

    public static bool IsLoopbackHost(string host)
    {
        var name = HostName(host);
        return name is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }

    /// <summary>True when a connection originates on this machine (loopback or one of its own addresses).</summary>
    public static bool IsLocalAddress(IPAddress address) => IPAddress.IsLoopback(address)
        || _localAddresses.Value.Any(local => local.Address.Equals(address));

    /// <summary>True when the Host header addresses this machine, not an attacker's DNS name.</summary>
    public static bool IsLocalHost(string host, bool lan)
    {
        var name = HostName(host);
        if (string.IsNullOrEmpty(name)) return false;
        if (IsLoopbackHost(name)) return true;
        if (!lan) return false;
        if (IsLocalAddress(name)) return true;
        return _machineNames.Value.Contains(name) || _machineNames.Value.Any(machine => name.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Strips a port from an HTTP Host header, keeping IPv6 brackets.</summary>
    public static string HostName(string? host)
    {
        var value = (host ?? string.Empty).Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 0 ? value[..(close + 1)] : value;
        }
        var colon = value.LastIndexOf(':');
        return colon > 0 && value.IndexOf(':') == colon ? value[..colon] : value;
    }

    private static bool IsLocalAddress(string value) => _localAddresses.Value.Any(local =>
        local.Address.ToString().Equals(value.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}

/// <summary>QR rendering for the terminal and the settings page.</summary>
public static class WebUiQr
{
    public static string PngDataUrl(string url)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
    }

    public static string Text(string url)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new AsciiQRCode(data).GetGraphicSmall();
    }
}
