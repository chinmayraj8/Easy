// Easy: a failed or cancelled conversion leaves nothing half-written. Engines write straight into the output path, so a
// full disk or a broken input left a truncated file under the output's name (it looked like a result) and writers'
// "<name>.tmp" files beside it; an overwritten file was lost although the new one never came.

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class IncompleteOutputTests
{
    /// <summary>Writes part of each output (and a writer's .tmp beside it), then fails or waits to be cancelled.</summary>
    private sealed class HalfWriter(int pages = 1, Task? wait = null) : IConverter
    {
        public string Id => "half";
        public string DisplayName => "half";
        public IReadOnlyList<ConversionEdge> Edges { get; } = [new("jpg", "png")];
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            for (var i = 0; i < pages; i++)
            {
                var path = step.Output.Allocate(step.To, pages > 1 ? $"_p{i + 1}" : null)!;
                await File.WriteAllTextAsync(path, "partial", cancellationToken);
                await File.WriteAllTextAsync(path + ".tmp", "", cancellationToken);
            }
            if (wait is not null)
                await wait.WaitAsync(cancellationToken);
            throw new IOException("No space left on device");
        }
    }

    private static async Task<ConversionJob> Run(IConverter converter, string source, Preset preset, Action<ConversionJob>? started = null)
    {
        await using var queue = new JobQueue(new ConverterCatalog([converter]));
        var job = new ConversionJob([source], preset, "Test");
        var run = queue.RunAsync(job);
        started?.Invoke(job);
        await run;
        return job;
    }

    [Fact]
    public async Task A_failed_conversion_leaves_no_partial_output_or_temp_file()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");

        var job = await Run(new HalfWriter(), source, new Preset { TargetFormat = "png" });

        Assert.Equal(FileState.Failed, job.Files[0].State);
        Assert.Contains("No space left", job.Files[0].ErrorDetail);
        Assert.Equal(["photo.jpg"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Pages_written_before_a_failure_are_removed_too()
    {
        using var dir = new TempDir();
        var source = dir.File("doc.jpg");

        await Run(new HalfWriter(pages: 3), source, new Preset { TargetFormat = "png" });

        Assert.Equal(["doc.jpg"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_cancelled_conversion_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");
        var never = new TaskCompletionSource();

        var job = await Run(new HalfWriter(wait: never.Task), source, new Preset { TargetFormat = "png" }, started: j =>
        {
            // Cancel once the engine has started writing.
            var output = Path.Combine(dir.Path, "photo.png");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!File.Exists(output) && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            j.Cancel();
        });

        Assert.Equal(FileState.Cancelled, job.Files[0].State);
        Assert.Equal(["photo.jpg"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task An_overwritten_file_comes_back_when_the_new_one_fails()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");
        var existing = dir.File("photo.png", "the user's earlier result");
        var preset = new Preset { TargetFormat = "png" };
        preset.Output.Conflict = ConflictPolicy.Overwrite;

        await Run(new HalfWriter(), source, preset);

        Assert.Equal("the user's earlier result", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        Assert.Equal(["photo.jpg", "photo.png"], Directory.GetFiles(dir.Path, "*", new EnumerationOptions { AttributesToSkip = 0 }).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_successful_overwrite_replaces_the_file_and_leaves_no_backup()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");
        var existing = dir.File("photo.png", "old");
        var preset = new Preset { TargetFormat = "png" };
        preset.Output.Conflict = ConflictPolicy.Overwrite;

        var job = await Run(new FakeConverter("magick", new ConversionEdge("jpg", "png")), source, preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("photo.jpg→png by magick", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        Assert.Equal(["photo.jpg", "photo.png"], Directory.GetFiles(dir.Path, "*", new EnumerationOptions { AttributesToSkip = 0 }).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Without_overwrite_an_existing_file_is_never_touched_by_a_failure()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");
        var existing = dir.File("photo.png", "keep me");

        await Run(new HalfWriter(), source, new Preset { TargetFormat = "png" });

        Assert.Equal("keep me", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(dir.Path, "photo (2).png")));
    }
}
