using System.Xml.Linq;

namespace TNovCloud.Tests;

public sealed class ShellPackageIdentityTests
{
    [Fact]
    public void Native_clsid_matches_sparse_msix_manifest()
    {
        var repository = FindRepositoryRoot();
        var header = File.ReadAllText(
            Path.Combine(repository, "src", "TNovCloud.ShellExtension", "ShellExtension.h"));
        var manifest = XDocument.Load(
            Path.Combine(repository, "packaging", "TNovCloud.Package", "AppxManifest.xml"));
        var applicationManifest = File.ReadAllText(
            Path.Combine(repository, "src", "TNovCloud.Client", "app.manifest"));

        var expected = Guid.Parse("4B8F4E39-392D-4B64-A8B7-7EA4D05C572F");
        Assert.Contains("0x4b8f4e39", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x392d", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x4b64", header, StringComparison.OrdinalIgnoreCase);

        XNamespace com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
        XNamespace desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
        var classId = Guid.Parse(
            manifest.Descendants(com + "Class").Single().Attribute("Id")!.Value);
        var verbs = manifest.Descendants(desktop5 + "Verb")
            .Select(x => Guid.Parse(x.Attribute("Clsid")!.Value))
            .ToArray();

        Assert.Equal(expected, classId);
        Assert.All(verbs, verb => Assert.Equal(expected, verb));
        Assert.Contains("packageName=\"TNovCloud.Package\"", applicationManifest);
        Assert.Contains("publisher=\"CN=TNovCloud.Dev\"", applicationManifest);
        Assert.Contains("applicationId=\"TNovCloud\"", applicationManifest);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TNovCloud.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
