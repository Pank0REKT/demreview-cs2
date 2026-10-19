using System.Text;
using DemReview.Application.Parsing;
using DemReview.Infrastructure.Parsing;

namespace DemReview.Tests.Parsing;

/// <summary>Повреждённая или не CS2 демка — понятное сообщение, а не падение.</summary>
public sealed class InvalidDemoTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "invalid-demos", Guid.NewGuid().ToString("N"));

    public InvalidDemoTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<DemoParseException> ParseFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        var parser = new DemoFileDemoParser(new CollectingLogger<DemoFileDemoParser>());
        return Assert.ThrowsAsync<DemoParseException>(() =>
            parser.ParseAsync(path, new ParseOptions(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Csgo_demo_is_reported_as_unsupported()
    {
        var ex = await ParseFile("csgo.dem", [.. "HL2DEMO\0"u8, .. new byte[1024]]);
        Assert.Contains("CS:GO", ex.Message);
    }

    [Fact]
    public async Task Random_file_is_reported_as_not_a_demo()
    {
        var ex = await ParseFile("text.dem", Encoding.UTF8.GetBytes("это не демка, а текстовый файл"));
        Assert.Contains("не является демкой CS2", ex.Message);
    }

    [Fact]
    public async Task Empty_file_is_reported_as_not_a_demo()
    {
        var ex = await ParseFile("empty.dem", []);
        Assert.Contains("не является демкой CS2", ex.Message);
    }

    [Fact]
    public async Task Truncated_cs2_demo_is_reported_as_damaged()
    {
        // Корректная сигнатура, дальше мусор — имитация повреждённого файла.
        var garbage = new byte[4096];
        new Random(42).NextBytes(garbage);
        var ex = await ParseFile("broken.dem", [.. "PBDEMS2\0"u8, .. garbage]);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Path_with_spaces_and_cyrillic_is_supported()
    {
        var ex = await ParseFile("Моя демка с пробелами.dem", [.. "HL2DEMO\0"u8]);
        Assert.Contains("CS:GO", ex.Message);
    }
}
