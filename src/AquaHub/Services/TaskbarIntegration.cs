using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using AquaHub.Core.Agents;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.UI.Controls;

namespace AquaHub.Services;

/// <summary>
/// The taskbar button: a jump list (right-click the pinned or running icon), media buttons in the window
/// thumbnail and an unread-alerts badge. The tray icon and quick panel remain the always-available part.
/// </summary>
public static class TaskbarIntegration
{
    /// <summary>
    /// Tasks shown when you right-click Aqua Hub on the taskbar or Start. They work whether or not the window is
    /// open: a second launch forwards the request to the running instance (or starts it quietly in the tray).
    /// </summary>
    public static void ApplyJumpList(string profileArgs)
    {
        if (Sandbox.Intercept("jumplist", "apply")) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            JumpTask Task(string title, string args, string description) => new()
            {
                Title = title, Arguments = args + profileArgs, Description = description,
                ApplicationPath = exe, IconResourcePath = exe, IconResourceIndex = 0,
            };
            var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
            list.JumpItems.Add(Task("Quick panel", "--flyout --background", "Brief, markets, media and your PC at a glance"));
            list.JumpItems.Add(Task("Ask or command…", "--palette --background", "Ask Aqua a question or run a command"));
            list.JumpItems.Add(Task("Read my brief aloud", "--read-brief --background", "Hear the latest brief, spoken on this PC"));
            list.JumpItems.Add(Task("Markets", "--page markets", "Open Aqua Hub on Markets"));
            list.JumpItems.Add(Task("Settings", "--page settings", "Open Aqua Hub settings"));
            list.JumpItemsRejected += (_, e) => Log.Warn("taskbar", $"{e.RejectedItems.Count} jump list item(s) rejected");
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception ex)
        {
            Log.Warn("taskbar", "Couldn't set the jump list", ex);
        }
    }

    /// <summary>Adds Previous / Play-Pause / Next to the window's taskbar thumbnail and badges unread alerts.</summary>
    public static void Attach(Window window)
    {
        var info = new TaskbarItemInfo();
        var previous = new ThumbButtonInfo { Description = "Previous track", DismissWhenClicked = false };
        var play = new ThumbButtonInfo { Description = "Play", DismissWhenClicked = false };
        var next = new ThumbButtonInfo { Description = "Next track", DismissWhenClicked = false };
        previous.Click += (_, _) => _ = Hub.Media.PreviousAsync();
        play.Click += (_, _) => _ = Hub.Media.PlayPauseAsync();
        next.Click += (_, _) => _ = Hub.Media.NextAsync();
        info.ThumbButtonInfos.Add(previous);
        info.ThumbButtonInfos.Add(play);
        info.ThumbButtonInfos.Add(next);
        window.TaskbarItemInfo = info;

        var dark = OsSignals.IsTaskbarDark();
        var badgeCount = -1;
        var playing = (bool?)null;

        void Update()
        {
            var media = Hub.Media.Current;
            var isPlaying = media?.IsPlaying == true;
            if (dark != OsSignals.IsTaskbarDark() || playing is null)
            {
                dark = OsSignals.IsTaskbarDark();
                previous.ImageSource = Glyph("previous", dark);
                next.ImageSource = Glyph("next", dark);
                playing = null;
            }
            if (playing != isPlaying)
            {
                playing = isPlaying;
                play.ImageSource = Glyph(isPlaying ? "pause" : "play", dark);
            }
            play.Description = media is null ? "Nothing playing" : $"{(isPlaying ? "Pause" : "Play")} · {media.Title}";
            play.IsEnabled = media is not null;
            previous.IsEnabled = media?.CanPrevious == true;
            next.IsEnabled = media?.CanNext == true;

            var unread = Hub.State.UnreadAlerts;
            if (unread != badgeCount)
            {
                badgeCount = unread;
                info.Overlay = unread > 0 ? Badge(unread) : null;
                info.Description = unread > 0 ? Plural.Of(unread, "unread alert") : "";
            }
        }

        Action onMedia = () => Hub.OnUi(Update);
        Action<string> onState = topic => { if (topic == Topics.Alerts) Hub.OnUi(Update); };
        Hub.Media.Changed += onMedia;
        Hub.State.Changed += onState;
        window.Closed += (_, _) =>
        {
            Hub.Media.Changed -= onMedia;
            Hub.State.Changed -= onState;
        };
        Update();
    }

    /// <summary>A 24-unit icon from the app's set, drawn for the taskbar's theme (not the app's).</summary>
    private static ImageSource Glyph(string name, bool darkTaskbar)
    {
        var brush = new SolidColorBrush(darkTaskbar ? Colors.White : Color.FromRgb(0x1F, 0x23, 0x28));
        brush.Freeze();
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24)); // keep the 24×24 box
            if (IconData.Get(name) is { } icon)
            {
                if (icon.Filled) dc.DrawGeometry(brush, null, icon.Geometry);
                else dc.DrawGeometry(null, new Pen(brush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, icon.Geometry);
            }
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Red count badge (a deeper red than the tray dot, so white digits stay legible at 16 px).</summary>
    private static ImageSource Badge(int count)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x52)), new Pen(Brushes.White, 1), new Point(8, 8), 7.5, 7.5);
            var label = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.Length > 1 ? 8 : 10, Brushes.White, 1.0);
            dc.DrawText(text, new Point(8 - text.Width / 2, 8 - text.Height / 2));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
