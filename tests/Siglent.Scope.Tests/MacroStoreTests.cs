using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Siglent.Server;

namespace Siglent.Scope.Tests;

public sealed class MacroStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"scopedeck-macros-{Guid.NewGuid():N}.json");

    private MacroStore NewStore() => new(NullLogger<MacroStore>.Instance, _file);

    private static MacroStep Call(string path, string json) => new(path, JsonDocument.Parse(json).RootElement, null, 120);

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void SavesReplacesAndDeletesMacros()
    {
        var store = NewStore();
        store.Save(new Macro("Calibrator", [Call("/api/channel/1", """{"voltsPerDiv":0.5}"""), new(null, null, 1000, null)]));
        store.Save(new Macro("Other", [Call("/api/panel", """{"key":39,"value":1}""")]));
        store.Save(new Macro("calibrator", [Call("/api/timebase", """{"timePerDiv":0.0005}""")])); // same name, any case

        // A new store reads the file back.
        var macros = NewStore().List();
        Assert.Equal(["calibrator", "Other"], macros.Select(m => m.Name));
        Assert.Equal("/api/timebase", macros[0].Steps.Single().Path);
        Assert.Equal(0.0005, macros[0].Steps[0].Body!.Value.GetProperty("timePerDiv").GetDouble());

        Assert.True(store.Delete("OTHER"));
        Assert.False(store.Delete("missing"));
        Assert.Single(NewStore().List());
    }

    [Theory]
    [InlineData("/api/connect", """{"host":"10.0.0.1"}""")]
    [InlineData("/api/setup/memory/3/save", "{}")]
    [InlineData("/api/update/install", "{}")]
    [InlineData("/api/scpi", """{"command":"*SAV 1"}""")]
    [InlineData("/api/scpi", """{"command":"PFSL LOCATION,IN,ACTION,SAVE"}""")]
    public void RejectsCallsAMacroMustNotMake(string path, string body)
    {
        var macro = new Macro("Bad", [Call(path, body)]);
        Assert.Throws<ArgumentException>(() => NewStore().Save(macro));
    }

    [Fact]
    public void RejectsEmptyOrUnnamedMacrosAndLongPauses()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Save(new Macro("", [new(null, null, 10, null)])));
        Assert.Throws<ArgumentException>(() => store.Save(new Macro("Empty", [])));
        Assert.Throws<ArgumentException>(() => store.Save(new Macro("Long", [new(null, null, MacroStore.MaxWaitMs + 1, null)])));
    }
}
