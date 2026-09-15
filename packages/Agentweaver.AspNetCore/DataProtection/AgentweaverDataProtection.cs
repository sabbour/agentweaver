namespace Agentweaver.AspNetCore.DataProtection;

using System.Xml.Linq;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class AgentweaverDataProtection
{
    public const string StableApplicationName = "agentweaver";
    public const string DefaultKeyVaultSecretPrefix = "agentweaver-dataprotection-key-";

    public static IDataProtectionBuilder AddAgentweaverDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        SecretClient? existingSecretClient = null)
    {
        var applicationName = configuration["DataProtection:ApplicationName"];
        if (string.IsNullOrWhiteSpace(applicationName))
            applicationName = StableApplicationName;

        var builder = services.AddDataProtection()
            .SetApplicationName(applicationName);

        var keysDirectory = configuration["DataProtection:KeysDirectory"];
        if (!string.IsNullOrWhiteSpace(keysDirectory))
        {
            Directory.CreateDirectory(keysDirectory);
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
            return builder;
        }

        var vaultUri = configuration["DataProtection:KeyVault:VaultUri"]
            ?? configuration["Auth:KeyVault:Uri"];
        var secretClient = existingSecretClient;
        if (secretClient is null && !string.IsNullOrWhiteSpace(vaultUri))
            secretClient = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());

        if (secretClient is not null)
        {
            var secretPrefix = configuration["DataProtection:KeyVault:SecretPrefix"];
            if (string.IsNullOrWhiteSpace(secretPrefix))
                secretPrefix = DefaultKeyVaultSecretPrefix;
            builder.AddKeyManagementOptions(options =>
                options.XmlRepository = new KeyVaultSecretXmlRepository(secretClient, secretPrefix));
            return builder;
        }

        if (!environment.IsDevelopment())
        {
            Console.Error.WriteLine(
                "WARNING: Data Protection has no durable key store outside Development. Configure " +
                "DataProtection:KeyVault:VaultUri or DataProtection:KeysDirectory so protected " +
                "cookies, tokens, and MCP session ids survive restarts and work across replicas.");
        }

        return builder;
    }
}

internal sealed class KeyVaultSecretXmlRepository(
    SecretClient secretClient,
    string secretPrefix) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        foreach (var secret in secretClient.GetPropertiesOfSecrets())
        {
            if (!secret.Name.StartsWith(secretPrefix, StringComparison.Ordinal))
                continue;

            try
            {
                var value = secretClient.GetSecret(secret.Name).Value.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    elements.Add(XElement.Parse(value));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
            }
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var keyId = (string?)element.Attribute("id");
        var name = SecretName(keyId, friendlyName);
        secretClient.SetSecret(name, element.ToString(SaveOptions.DisableFormatting));
    }

    private string SecretName(string? keyId, string friendlyName)
    {
        var suffix = !string.IsNullOrWhiteSpace(keyId)
            ? keyId
            : friendlyName;
        suffix = suffix.Trim('{', '}');

        var builder = new System.Text.StringBuilder(secretPrefix.Length + suffix.Length);
        builder.Append(secretPrefix);
        foreach (var c in suffix)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-')
                builder.Append(c);
            else if (c == '_' || c == '.')
                builder.Append('-');
        }

        var name = builder.ToString().TrimEnd('-');
        return name.Length <= 127 ? name : name[..127];
    }
}
