using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Api;

internal static class DefinitionRegistryCacheSignature
{
    public static string ForDirectory(string directory, IEnumerable<string>? additionalParts = null)
    {
        var builder = new StringBuilder();

        if (additionalParts is not null)
        {
            foreach (var part in additionalParts)
                builder.Append(part).Append('\n');
        }

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsYaml)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                builder
                    .Append(Path.GetFileName(file)).Append('|')
                    .Append(info.Length).Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static bool IsYaml(string file) =>
        file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
}
