using System.IO.Pipes;
using System.Text;
using AquaHub.Platform;

namespace AquaHub.Tests;

public sealed class SearchFolderActivationTests
{
    [Fact]
    public void ExplorerPathRemovesQuoteGuardAndRejectsInvalidPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"C:\College Notes\Café", SearchFolderActivation.Normalize(@"C:\College Notes\Café\."));
        Assert.Equal(@"C:\", SearchFolderActivation.Normalize(@"C:\\."));
        Assert.Equal(@"\\server\share\notes", SearchFolderActivation.Normalize(@"\\server\share\notes\."));
        Assert.Null(SearchFolderActivation.Normalize(@"C:\notes:stream"));
        Assert.Null(SearchFolderActivation.Normalize(@"\\?\C:\notes"));
        Assert.Null(SearchFolderActivation.Normalize("--quit"));
        Assert.Null(SearchFolderActivation.Normalize(@"C:\" + new string('a', 2048)));
    }

    [Fact]
    public async Task OversizedPipeMessageCannotNavigateAndAFollowingFolderArrives()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "aqua-search-test-" + Guid.NewGuid().ToString("N");
        var activation = new SearchFolderActivation(name);
        var received = new List<string>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listening = activation.ListenAsync(path =>
        {
            received.Add(path);
            arrived.TrySetResult();
        }, stop.Token);
        try
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(stop.Token);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(@"C:\" + new string('a', 6142)), stop.Token);
            }
            Assert.True(await activation.SendAsync(@"C:\Scratch folder\Café\."));
            await arrived.Task.WaitAsync(stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await listening.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(@"C:\Scratch folder\Café", Assert.Single(received));
    }
}
