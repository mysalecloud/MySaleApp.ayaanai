using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MySale.AI.Application.Services;

namespace MySale.AI.Application.TenantProviders;

/// <summary>A configuration after validation and normalisation (the key, if any, only in memory).</summary>
public sealed class NormalizedTenantProvider
{
    public required ProviderDescriptor Descriptor { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseUrl { get; init; }
    public required Dictionary<string, string> Settings { get; init; }
    public string? ApiKey { get; init; }
    public string DefaultModel { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool AllowSystemFallback { get; init; }
}

/// <summary>Input validation of the AI Providers &amp; API Keys page. Messages are user-safe and never echo the key.</summary>
public static class TenantProviderValidation
{
    private static readonly Regex SettingValue = new(@"^[A-Za-z0-9._:\-]{1,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModelName = new(@"^[A-Za-z0-9._:/@+\-]{1,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Printable ASCII without spaces: safe in an HTTP header, no CR/LF injection.
    private static readonly Regex KeyChars = new(@"^[\x21-\x7E]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static NormalizedTenantProvider Normalize(TenantProviderUpsertRequest r, TenantAiProviderOptions options, ProviderDescriptor? fixedDescriptor = null)
    {
        var descriptor = fixedDescriptor ?? ProviderRegistry.Find(r.ProviderId)
                         ?? throw new AppValidationException("Choose a supported AI provider.");
        if (options.AllowedProviders.Count > 0 && !options.AllowedProviders.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase))
            throw new AppValidationException($"{descriptor.DisplayName} is not available on this server.");

        var name = Clean(r.DisplayName);
        if (string.IsNullOrEmpty(name)) name = descriptor.DisplayName;
        if (name.Length > 80) throw new AppValidationException("The name can be at most 80 characters.");

        var baseUrl = NormalizeBaseUrl(descriptor, r.BaseUrl, options);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var input = r.Settings ?? new Dictionary<string, string>();
        foreach (var inputKey in input.Keys)
            if (descriptor.Fields.All(f => !string.Equals(f.Key, inputKey, StringComparison.Ordinal)))
                throw new AppValidationException("The request contains a field that this provider does not use.");
        foreach (var field in descriptor.Fields)
        {
            input.TryGetValue(field.Key, out var raw);
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0 && field.DefaultValue is { Length: > 0 } dv) value = dv;
            if (value.Length == 0)
            {
                if (field.Required) throw new AppValidationException($"{field.Label} is required.");
                continue;
            }
            if (value.Length > field.MaxLength || !SettingValue.IsMatch(value))
                throw new AppValidationException($"{field.Label} contains characters that are not allowed.");
            if (field.Options is { Count: > 0 } && !field.Options.Contains(value, StringComparer.Ordinal))
                throw new AppValidationException($"Choose a valid {field.Label}.");
            settings[field.Key] = value;
        }

        string? key = null;
        if (!string.IsNullOrWhiteSpace(r.ApiKey))
        {
            key = r.ApiKey.Trim();
            if (key.Length < 8 || key.Length > 4096 || !KeyChars.IsMatch(key))
                throw new AppValidationException($"The {descriptor.KeyLabel} format is not valid.");
        }

        var model = (r.DefaultModel ?? string.Empty).Trim();
        if (descriptor.Adapter == ProviderAdapterType.AzureOpenAI && model.Length == 0 && settings.TryGetValue("deployment", out var deployment))
            model = deployment;
        if (model.Length > 0 && !ModelName.IsMatch(model))
            throw new AppValidationException("The model name contains characters that are not allowed.");

        return new NormalizedTenantProvider
        {
            Descriptor = descriptor,
            DisplayName = name,
            BaseUrl = baseUrl,
            Settings = settings,
            ApiKey = key,
            DefaultModel = model,
            Enabled = r.Enabled,
            AllowSystemFallback = r.AllowSystemFallback
        };
    }

    public static bool IsValidModel(string? model) => !string.IsNullOrWhiteSpace(model) && ModelName.IsMatch(model.Trim());

    /// <summary>Effective endpoint: the registry default for fixed providers, otherwise the validated URL the company entered.</summary>
    public static string NormalizeBaseUrl(ProviderDescriptor descriptor, string? requested, TenantAiProviderOptions options)
    {
        if (!descriptor.BaseUrlEditable) return descriptor.DefaultBaseUrl;
        var value = (requested ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            if (descriptor.BaseUrlRequired || string.IsNullOrEmpty(descriptor.DefaultBaseUrl))
                throw new AppValidationException($"{descriptor.BaseUrlLabel} is required.");
            return descriptor.DefaultBaseUrl;
        }
        if (value.Length > 500) throw new AppValidationException($"{descriptor.BaseUrlLabel} is too long.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new AppValidationException($"{descriptor.BaseUrlLabel} is not a valid URL.");
        var check = CheckEndpoint(uri, options);
        if (check is not null) throw new AppValidationException(check);
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Null when the endpoint is acceptable; otherwise a user-safe reason. DNS is checked again at connect time.</summary>
    public static string? CheckEndpoint(Uri uri, TenantAiProviderOptions options)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
            return "Only HTTPS endpoints are allowed.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The endpoint must not contain a user name or password.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "The endpoint must not contain a query string.";
        if (options.AllowPrivateEndpoints) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal) || !host.Contains('.'))
            return "Private or internal network endpoints are not allowed.";
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublic(ip))
            return "Private or internal network endpoints are not allowed.";
        return null;
    }

    /// <summary>True for globally routable unicast addresses (blocks loopback, private, link-local, CGNAT, metadata, multicast …).</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // CGNAT
                     || (b[0] == 169 && b[1] == 254)                 // link-local / cloud metadata
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any))
                return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                    // fc00::/7 unique local
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // documentation
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return false; // NAT64
            return true;
        }
        return false;
    }

    private static string Clean(string? s)
        => new string((s ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
}
