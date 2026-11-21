using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using AquaHub.Core.Agents;
using AquaHub.Core.Health;
using AquaHub.UI.Shell;

namespace AquaHub.Services;

public static partial class SnapshotRunner
{
    private static async Task RecipesAsync(string outDir)
    {
        if (!Sandbox.Enabled) throw new InvalidOperationException("Recipe rendering requires an isolated profile.");
        Hub.Core.Settings.Update(settings =>
        {
            settings.Ai.Enabled = settings.Ai.ManageOllama = settings.Ai.StartOllamaOnDemand = false;
            settings.Ai.Provider = "llamacpp";
            settings.Ai.Llama.UseOllamaModels = settings.Ai.Llama.UseOllamaEngine = false;
            settings.Ask.Computer = settings.Privacy.LoadRemoteImages = false;
            settings.General.ReduceMotion = true;
        });
        Hub.State.SetSystem(new() { MachineName = "Recipe test PC", OsName = "Windows 11", Time = DateTimeOffset.Now });
        var checks = 0;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousInspector = Hub.Health.Inspect;
        Hub.Health.Inspect = async (_, ct) =>
        {
            if (Interlocked.Increment(ref checks) == 2) await hold.Task.WaitAsync(ct);
            return new()
            {
                Machine = "Recipe test PC",
                Drives = [new("C:", "Fixture", 200, 5, true)],
                StorageRetries = 4,
                Readings = [new("Disk I/O retries", HealthReadingState.Observed), new("Windows Firewall", HealthReadingState.Denied),
                    new("Battery wear", HealthReadingState.Unsupported)],
            };
        };
        var win = new MainWindow { Width = 1440, Height = 880, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        var activated = false;
        win.Activated += (_, _) => activated = true;
        try
        {
            win.Show();
            ApplyTheme("dark", 1440, 880);
            win.Navigate("workbench", "recipes");
            await UntilAsync(() => Button("wb-recipe-new").IsVisible);
            Invoke(Button("wb-recipe-new"));
            await UntilAsync(() => Button("wb-recipe-save").IsVisible);
            Save("editor-dark");
            Invoke(Button("wb-recipe-save"));
            await UntilAsync(() => Hub.Core.Recipes.State.Recipes.Length == 1);
            Require(Hub.Core.Recipes.State.Runs.IsEmpty && checks == 0, "Saving unexpectedly ran the recipe.");
            var recipe = Hub.Core.Recipes.State.Recipes.Single();

            win.Navigate("agents", $"recipe:{recipe.Id}");
            await UntilAsync(() => Button("agents-recipe-review").IsVisible);
            Invoke(Button("agents-recipe-review"));
            await UntilAsync(() => Button("agents-recipe-confirm").IsVisible);
            Require(Hub.Core.Recipes.State.Runs.IsEmpty && checks == 0, "Review unexpectedly ran the recipe.");
            Save("review-dark");
            Invoke(Button("agents-recipe-confirm"));
            await UntilAsync(() => Hub.Core.Recipes.State.Runs.Any(run => run.State == RecipeRunState.Completed)
                && Button("agents-recipe-review").IsEnabled && Button("agents-recipe-review").IsVisible);
            Require(checks == 1 && Hub.Health.Last?.Facts.StorageRetries == 4, "The first run did not save its health report.");
            Save("runs-dark");
            var finished = Hub.Core.Recipes.State.Runs.Single();

            ApplyTheme("light", 820, 580);
            Invoke(Button("agents-recipe-review"));
            await UntilAsync(() => Button("agents-recipe-confirm").IsVisible);
            Save("review-light");
            Invoke(Button("agents-recipe-confirm"));
            await UntilAsync(() => Volatile.Read(ref checks) == 2 && Button("agents-recipe-cancel").IsVisible);
            var history = (ListBox)((FrameworkElement)win.PageHost.Content).FindName("RecipeHistory");
            history.SelectedIndex = 1;
            var selected = history.SelectedItem;
            Invoke(Button("agents-recipe-cancel"));
            await UntilAsync(() => Hub.Core.Recipes.State.Runs.Any(run => run.State == RecipeRunState.Cancelled));
            Require(ReferenceEquals(selected, history.SelectedItem), "Run selection changed during a status refresh.");
            var cancelled = Hub.Core.Recipes.State.Runs.First();
            Require(cancelled.Steps[1].State == RecipeStepState.Pending && Hub.Core.Recipes.Preview?.RunId == finished.Id,
                "A cancelled health check ran the following step or replaced the previous result.");
            history.SelectedIndex = 0;
            ((FrameworkElement)((FrameworkElement)win.PageHost.Content).FindName("RecentRunsCard")).BringIntoView();
            await LayoutAsync();
            Save("runs-light");

            win.Navigate("system", "health-report");
            await UntilAsync(() => FindVisual<Expander>(win, expander => AutomationProperties.GetAutomationId(expander) == "system-health-readings") is { IsVisible: true });
            var readings = FindVisual<Expander>(win, expander => AutomationProperties.GetAutomationId(expander) == "system-health-readings")!;
            if (new ExpanderAutomationPeer(readings).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                throw new InvalidOperationException("The Windows readings do not expose an expand pattern.");
            expand.Expand();
            readings.BringIntoView();
            await LayoutAsync();
            Save("health-light");

            win.Navigate("agents");
            await UntilAsync(() => Button("agents-detail-back").IsVisible);
            Invoke(Button("agents-detail-back"));
            await UntilAsync(() => Button("agents-detail-news-scout").IsVisible);
            Invoke(Button("agents-detail-news-scout"));
            await UntilAsync(() => Button("agents-detail-run").IsVisible);
            Save("built-in-light");

            win.Navigate("workbench", $"recipe:{recipe.Id}");
            await UntilAsync(() => Button("wb-recipe-save").IsVisible);
            Save("editor-light");
        }
        finally
        {
            hold.TrySetResult();
            await Hub.Core.Recipes.StopAsync();
            Hub.Health.Inspect = previousInspector;
            win.Close();
        }

        void ApplyTheme(string theme, int width, int height)
        {
            var options = Hub.Core.Settings.Clone().General;
            options.Theme = theme;
            Hub.Theme.Apply(options);
            win.Width = width;
            win.Height = height;
        }

        Button Button(string id) => FindVisual<Button>(win, button => AutomationProperties.GetAutomationId(button) == id)
            ?? throw new InvalidOperationException($"Missing action {id}.");

        static void Invoke(Button button)
        {
            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("A recipe button has no accessible Invoke pattern.");
            invoke.Invoke();
        }

        async Task LayoutAsync()
        {
            await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            win.UpdateLayout();
            Require(!activated && !win.IsActive && win.Left < -20000 && win.Top < -20000, "The render window activated.");
        }

        async Task UntilAsync(Func<bool> ready)
        {
            var limit = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                await LayoutAsync();
                if (ready()) return;
            }
            while (limit.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("The recipe view did not reach its expected state.");
        }

        void Save(string name) => Capture((FrameworkElement)win.Content, Path.Combine(outDir, name + ".png"), scale: 1);

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
