using Microsoft.Extensions.FileProviders;

namespace Siglent.Scope.Tests;

public class EmbeddedUiTests
{
    [Fact]
    public void UiIsEmbeddedInServerAssembly()
    {
        var files = new ManifestEmbeddedFileProvider(typeof(Siglent.Server.SiglentServer).Assembly, "wwwroot");

        Assert.True(files.GetFileInfo("index.html").Exists);
        Assert.NotEmpty(files.GetDirectoryContents("assets"));
    }
}
