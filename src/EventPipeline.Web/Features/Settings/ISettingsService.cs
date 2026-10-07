namespace EventPipeline.Web.Features.Settings;

public interface ISettingsService
{
    /// <summary>Returns the decrypted secret, or null when the key is absent/unconfigured.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);

    /// <summary>Stores a secret encrypted at rest. Empty/whitespace clears the row.</summary>
    Task SetSecretAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Returns a plain (non-secret) setting value, or null when absent.</summary>
    Task<string?> GetPlainAsync(string key, CancellationToken ct = default);

    Task SetPlainAsync(string key, string value, CancellationToken ct = default);
}
