using System.Security.Cryptography;

namespace Snet.Edge.Tests;

public sealed class VendorAssetIntegrityTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vue/vue.global.prod.js"] = "5828F332C1A6E05AC08E87F51E3C28A8877609A6D3132D899C97D48DA12C727A",
            ["element-plus/index.full.min.js"] = "08AC4632B698AC54CEE4D46777F3C6C16B7CCE7C32A329EA5E7D2C96A94F154E",
            ["element-plus/index.css"] = "A8B23008C6B2EE497133DC9A7C97645A30236C06D28E6A6E594B4A4BFC2B72AD",
            ["element-plus/dark/css-vars.css"] = "32124C50111E44BB9E2A8E9BB17067609D178AED950D421F6F6785C913A5E621",
            ["element-plus-icons/index.iife.min.js"] = "B6D3DA03D73BFA394A28F442BF896B31119F502FF3B59608BB73A88562E5D306",
            ["signalr/signalr.min.js"] = "10DD7681421E4638A55A3367A68AC0EAC1973BE1B4471B91B1B9E2306BB0B050"
        };

    [Fact]
    public void VendoredFrontendDependenciesMatchPinnedHashes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var vendorRoot = Path.Combine(repositoryRoot, "src", "Snet.Edge.WebApi", "wwwroot", "vendor");

        foreach (var expected in ExpectedHashes)
        {
            var filePath = Path.Combine(vendorRoot, expected.Key.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(filePath), $"Missing vendor asset: {expected.Key}");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
            Assert.Equal(expected.Value, actual);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Snet.Edge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
