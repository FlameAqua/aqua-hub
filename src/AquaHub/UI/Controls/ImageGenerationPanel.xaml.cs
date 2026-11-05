using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaHub.Core.Ai.Imaging;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Shell;
using Microsoft.Win32;

namespace AquaHub.UI.Controls;

public partial class ImageGenerationPanel : UserControl
{
    private bool _filling = true;
    private string? _previewId;
    private IReadOnlyList<GeneratedImage>? _gallery;
    private ImageModelInventory? _inventory;
    private GeneratedImage? _deleteTarget;
    private static ImageGenerationSession Session => Hub.Images;

    public ImageGenerationPanel()
    {
        InitializeComponent();
        var s = Hub.S.ImageGeneration ?? new ImageStudioSettings();
        Endpoint.Text = s.Endpoint;
        FillModels(Diffusion, s.DiffusionModel);
        FillModels(Encoder, s.TextEncoder);
        FillModels(Vae, s.Vae);
        Variant.SelectedIndex = s.Variant == "distilled" ? 1 : 0;
        Retention.SelectedItem = Retention.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag.ToString() == s.RetentionDays.ToString(CultureInfo.InvariantCulture));
        if (Retention.SelectedItem is null)
        {
            var custom = new ComboBoxItem { Content = $"{s.RetentionDays} days", Tag = s.RetentionDays.ToString(CultureInfo.InvariantCulture) };
            Retention.Items.Add(custom); Retention.SelectedItem = custom;
        }
        Prompt.Text = Session.Draft;
        _filling = false;
        UpdateQuality();
        Refresh();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Session.Changed -= Refresh;
        Session.Changed += Refresh;
        await Session.LoadAsync();
        Refresh();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Session.Changed -= Refresh;
    private void OnPromptChanged(object sender, TextChangedEventArgs e) { if (!_filling) Session.Draft = Prompt.Text; }

    private ImageGenerationOptions Options()
    {
        if (!Uri.TryCreate(Endpoint.Text.Trim(), UriKind.Absolute, out var endpoint))
            throw new ArgumentException("Enter ComfyUI's local address, for example http://127.0.0.1:8188.");
        var options = new ImageGenerationOptions
        {
            Endpoint = endpoint,
            Variant = Variant.SelectedIndex == 1 ? ImageModelVariant.Distilled4B : ImageModelVariant.Base4B,
            DiffusionModel = (string)Diffusion.SelectedItem, TextEncoderModel = (string)Encoder.SelectedItem, VaeModel = (string)Vae.SelectedItem,
        };
        // Validate before persisting; redirects are disabled by the HTTP client.
        using var validationHttp = new System.Net.Http.HttpClient();
        _ = new ComfyImageClient(validationHttp, options);
        Hub.Core.Settings.Update(s =>
        {
            s.ImageGeneration.Endpoint = endpoint.AbsoluteUri;
            s.ImageGeneration.Variant = options.Variant == ImageModelVariant.Base4B ? "base" : "distilled";
            s.ImageGeneration.DiffusionModel = options.SelectedDiffusionModel;
            s.ImageGeneration.TextEncoder = options.TextEncoderModel;
            s.ImageGeneration.Vae = options.VaeModel;
        });
        return options;
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        try { await Session.CheckAsync(Options()); }
        catch (Exception ex) { Session.Message(ex.Message, true); }
    }
    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Prompt.Text)) { Session.Message("Describe the image you'd like to create.", true); Prompt.Focus(); return; }
            ulong? seed = null;
            if (!string.IsNullOrWhiteSpace(Seed.Text))
            {
                if (!ulong.TryParse(Seed.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed > 9007199254740991)
                    throw new ArgumentException("Use a whole-number seed between 0 and 9007199254740991, or leave it blank.");
                seed = parsed;
            }
            var size = ((ComboBoxItem)Aspect.SelectedItem).Tag.ToString()!.Split(',');
            var options = Options();
            var steps = options.Variant == ImageModelVariant.Distilled4B ? 4 : int.Parse(((ComboBoxItem)Quality.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture);
            await Session.GenerateAsync(options, new ImageGenerationRequest(Prompt.Text.Trim(), int.Parse(size[0]), int.Parse(size[1]), steps, seed)
            { ReferenceImage = Session.ReferenceImage });
        }
        catch (Exception ex) { Session.Message(ex.Message, true); }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Session.Cancel();
    private async void OnRecover(object sender, RoutedEventArgs e) => await Session.RecoverAsync();
    private void OnExample(object sender, RoutedEventArgs e) { Session.ClearReference(); Prompt.Text = (string)((Button)sender).Tag; Refresh(); }

    // ───────────── Image to edit: picker, drop, paste ─────────────

    private static readonly string ImageFilter = "Images|" + string.Join(";", Core.Ai.Assistant.Documents.ImageExtensions.Append(".heif").Select(x => "*" + x));

    private async void OnAttachImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose an image to edit", Filter = ImageFilter, CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        if (await Session.AttachFileAsync(picker.FileName)) FocusPrompt();
    }

    private static string? DroppedImage(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(f => File.Exists(f) && Core.Ai.Assistant.Documents.IsImage(f)) : null;

    private void OnImageDragOver(object sender, DragEventArgs e)
    {
        if (DroppedImage(e.Data) is null) return;
        e.Effects = Session.Busy || Session.Attaching ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnImageDrop(object sender, DragEventArgs e)
    {
        if (DroppedImage(e.Data) is not { } path) return;
        e.Handled = true;
        if (await Session.AttachFileAsync(path)) FocusPrompt();
    }

    /// <summary>Ctrl+V with a picture (or a copied image file) on the clipboard makes it the image to edit; text pastes as usual.</summary>
    private async void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || Session.Busy || Session.Attaching) return;
        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => File.Exists(f) && Core.Ai.Assistant.Documents.IsImage(f)) is { } file)
            {
                e.Handled = true;
                if (await Session.AttachFileAsync(file)) FocusPrompt();
            }
            else if (!Clipboard.ContainsText() && ScreenCapture.ClipboardImage() is { } png)
            {
                e.Handled = true;
                if (await Session.AttachImageAsync(png, "Pasted image")) FocusPrompt();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void FocusPrompt() { Prompt.BringIntoView(); Prompt.Focus(); }

    private string? _matchTag;
    /// <summary>Adds (and picks) a canvas the size of the image to edit; removes it when there's none.</summary>
    private void SyncMatchCanvas()
    {
        var reference = Session.Reference;
        var tag = reference is null ? null : $"{reference.Width},{reference.Height}";
        if (tag == _matchTag) return;
        _matchTag = tag;
        var old = Aspect.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Uid, "match"));
        var wasSelected = old is not null && ReferenceEquals(Aspect.SelectedItem, old);
        if (old is not null) Aspect.Items.Remove(old);
        if (reference is null)
        {
            if (wasSelected || Aspect.SelectedItem is null) Aspect.SelectedIndex = 0;
            return;
        }
        var match = new ComboBoxItem { Uid = "match", Content = $"Match image · {reference.Width} × {reference.Height}", Tag = tag };
        Aspect.Items.Insert(0, match);
        Aspect.SelectedItem = match;
    }

    private void OnVariant(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Quality is null) return;
        FillModels(Diffusion, Variant.SelectedIndex == 1 ? "flux-2-klein-4b-fp8.safetensors" : "flux-2-klein-base-4b-fp8.safetensors", _inventory?.DiffusionModels);
        UpdateQuality();
    }
    private static void FillModels(ComboBox picker, string selected, IEnumerable<string>? available = null)
    {
        // Keep the configured name visible before discovery, including when its file is still missing.
        picker.ItemsSource = new[] { selected }.Concat(available ?? []).Distinct(StringComparer.Ordinal).ToArray();
        picker.SelectedItem = selected;
    }
    private void UpdateQuality()
    {
        Quality.IsEnabled = Variant.SelectedIndex == 0 && !Session.Busy;
        QualityHint.Text = Variant.SelectedIndex == 1 ? "Distilled Klein · 4 steps for quick exploration." : "Klein Base · more detail and variation, at a gentler pace.";
    }

    private void Refresh()
    {
        Status.Text = Session.Status;
        Status.SetResourceReference(TextBlock.ForegroundProperty, Session.Error ? "B.Down" : "B.Text2");
        Generate.IsEnabled = !Session.Busy && !Session.Checking && !Session.ManagingImages && !Session.RecoveryNeeded && !Session.Attaching;
        Generate.Content = Session.Busy ? "Creating…" : Session.Editing ? "Apply image edit" : "Generate image";
        RefreshReference();
        AttachImage.IsEnabled = !Session.Busy && !Session.Attaching;
        AttachImage.Content = Session.Reference is null ? "Add an image to edit…" : "Use a different image…";
        StartServer.IsEnabled = !Session.Busy && !Session.Checking;
        Check.IsEnabled = !Session.Busy && !Session.Checking && !Session.RecoveryNeeded;
        SetupFields.IsEnabled = !Session.Busy && !Session.Checking && !Session.RecoveryNeeded;
        Recover.Visibility = Session.RecoveryNeeded ? Visibility.Visible : Visibility.Collapsed;
        Recover.IsEnabled = !Session.Checking;
        Prompt.IsEnabled = !Session.Busy;
        Aspect.IsEnabled = !Session.Busy;
        Seed.IsEnabled = !Session.Busy;
        Retention.IsEnabled = !Session.Busy && !Session.ManagingImages;
        Cancel.Visibility = Session.Busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = Session.Busy || Session.Checking || Session.Attaching ? Visibility.Visible : Visibility.Collapsed;
        UpdateQuality();
        if (!ReferenceEquals(_inventory, Session.Inventory) && Session.Inventory is { } inventory)
        {
            _inventory = inventory;
            FillModels(Diffusion, (string)Diffusion.SelectedItem, inventory.DiffusionModels);
            FillModels(Encoder, (string)Encoder.SelectedItem, inventory.TextEncoders);
            FillModels(Vae, (string)Vae.SelectedItem, inventory.Vaes);
            if (inventory.IsReady) Setup.IsExpanded = false;
        }
        if (!ReferenceEquals(_gallery, Session.Recent))
        {
            _gallery = Session.Recent;
            Gallery.ItemsSource = Session.Recent.Select(item => new GalleryItem(item, LoadImage(Session.Archive.ImagePath(item), 140))).ToArray();
        }
        if (_previewId != Session.Selected?.Id)
        {
            _previewId = Session.Selected?.Id;
            Preview.Source = Session.Selected is { } item ? LoadImage(Session.Archive.ImagePath(item)) : null;
            ImageDetails.Text = Session.Selected is { } selected ? $"{selected.Width} × {selected.Height}\nSeed {selected.Seed} · {selected.Seconds:0}s"
                + (selected.EditedFrom is { } from ? $"\nEdited from {(from.Source == ImageReferenceSource.Gallery ? "a gallery image" : from.Name)}" : "") : "";
        }
        EmptyCanvas.Visibility = Preview.Source is null ? Visibility.Visible : Visibility.Collapsed;
        SaveImage.IsEnabled = OpenImage.IsEnabled = Preview.Source is not null;
        Regenerate.IsEnabled = EditImage.IsEnabled = DeleteImage.IsEnabled = Preview.Source is not null && Generate.IsEnabled;
        var settings = Hub.S.ImageGeneration;
        LauncherLabel.Text = string.IsNullOrWhiteSpace(settings.LauncherPath) ? "ComfyUI.exe or run_nvidia_gpu.bat" : settings.LauncherPath;
        LauncherLabel.ToolTip = LauncherLabel.Text;
        OutputLabel.Text = string.IsNullOrWhiteSpace(settings.OutputFolder) ? "Not selected" : settings.OutputFolder;
        OutputLabel.ToolTip = OutputLabel.Text;
        RetentionHint.Text = string.IsNullOrWhiteSpace(settings.OutputFolder)
            ? "Expiry applies to Aqua's gallery. Choose ComfyUI's output folder below to expire its copies too."
            : "Expiry runs at startup and hourly while Aqua is open. Exported copies are kept.";
    }

    private string? _thumbId;
    private void RefreshReference()
    {
        var reference = Session.Reference;
        ReferenceBanner.Visibility = Session.Editing ? Visibility.Visible : Visibility.Collapsed;
        ReferenceText.Text = reference is null ? "Editing with a reference image. Describe what should change."
            : reference.Source == ImageReferenceSource.Gallery ? $"Editing a copy of “{reference.Name}”. Describe what should change; the original stays as it is."
            : $"Editing {reference.Name} · {reference.Width} × {reference.Height}. Describe what should change, and what should stay.";
        if (_thumbId != reference?.Id)
        {
            _thumbId = reference?.Id;
            ReferenceThumb.Source = reference is null ? null : LoadImage(Session.Archive.ReferencePath(reference.Id), 104);
        }
        ReferenceThumbFrame.Visibility = ReferenceThumb.Source is null ? Visibility.Collapsed : Visibility.Visible;
        SyncMatchCanvas();
    }

    private static BitmapImage? LoadImage(string path, int width = 0)
    {
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (width > 0) bitmap.DecodePixelWidth = width;
            using var stream = File.OpenRead(path); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }
    private void OnGallery(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not GalleryItem entry) return;
        DeleteConfirmation.Visibility = Visibility.Collapsed; _deleteTarget = null;
        Session.Selected = entry.Item;
        Refresh();
    }
    private bool RestoreSettings(GeneratedImage item)
    {
        // The reference first, so a "match image" canvas exists before the saved size is picked.
        var restored = Session.RestoreReference(item);
        RefreshReference();
        _filling = true;
        try
        {
            Variant.SelectedIndex = item.Variant == "distilled" ? 1 : 0;
            FillModels(Diffusion, item.Model, _inventory?.DiffusionModels);
            if (!string.IsNullOrWhiteSpace(item.TextEncoder)) FillModels(Encoder, item.TextEncoder, _inventory?.TextEncoders);
            if (!string.IsNullOrWhiteSpace(item.Vae)) FillModels(Vae, item.Vae, _inventory?.Vaes);
            var dimensions = $"{item.Width},{item.Height}";
            var canvas = Aspect.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag.ToString() == dimensions);
            if (canvas is null && item.Width >= 256 && item.Height >= 256)
            {
                canvas = new ComboBoxItem { Content = $"Saved · {item.Width} × {item.Height}", Tag = dimensions };
                Aspect.Items.Add(canvas);
            }
            Aspect.SelectedItem = canvas ?? Aspect.Items[0];
            if (item.Steps is >= 1 and <= 100 && item.Variant != "distilled")
            {
                var quality = Quality.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag.ToString() == item.Steps.ToString(CultureInfo.InvariantCulture));
                if (quality is null) { quality = new ComboBoxItem { Content = $"Saved ({item.Steps})", Tag = item.Steps.ToString(CultureInfo.InvariantCulture) }; Quality.Items.Add(quality); }
                Quality.SelectedItem = quality;
            }
        }
        finally { _filling = false; }
        Prompt.Text = item.Prompt;
        Seed.Text = item.Seed.ToString(CultureInfo.InvariantCulture);
        Refresh();
        return restored;
    }
    private void OnRegenerate(object sender, RoutedEventArgs e)
    {
        if (Session.Selected is not { } item || !Generate.IsEnabled) return;
        // Without the image it was edited from, regenerating would ignore it; RestoreReference has said so.
        if (!RestoreSettings(item)) return;
        Seed.Text = "";
        OnGenerate(sender, e);
    }
    private async void OnEditImage(object sender, RoutedEventArgs e)
    {
        if (Session.Selected is not { } item || !Generate.IsEnabled) return;
        RestoreSettings(item);
        // Aqua edits its own copy of the image, so this works whether or not ComfyUI still has the output.
        if (await Session.EditGalleryImageAsync(item))
        {
            Prompt.Text = ""; Seed.Text = "";
            FocusPrompt();
        }
        Refresh();
    }
    private void OnClearReference(object sender, RoutedEventArgs e) { Session.ClearReference(); Refresh(); }
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        _deleteTarget = Session.Selected;
        DeleteConfirmation.Visibility = _deleteTarget is null ? Visibility.Collapsed : Visibility.Visible;
        DeleteConfirmation.BringIntoView();
    }
    private async void OnConfirmDelete(object sender, RoutedEventArgs e)
    {
        if (_deleteTarget is not { } item) return;
        _deleteTarget = null; DeleteConfirmation.Visibility = Visibility.Collapsed;
        await Session.DeleteAsync(item);
    }
    private void OnCancelDelete(object sender, RoutedEventArgs e) { _deleteTarget = null; DeleteConfirmation.Visibility = Visibility.Collapsed; }
    private void OnFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Session.Archive.Folder);
            if (!Sandbox.Intercept("image-folder", Session.Archive.Folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Session.Archive.Folder) { UseShellExecute = true });
        }
        catch (Exception ex) { Session.Message("Couldn't open the image folder: " + ex.Message, true); }
    }
    private async void OnRetention(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Retention.SelectedItem is not ComboBoxItem choice) return;
        var days = int.Parse(choice.Tag.ToString()!, CultureInfo.InvariantCulture);
        Hub.Core.Settings.Update(s => s.ImageGeneration.RetentionDays = days);
        await Session.ApplyRetentionAsync(report: true);
    }
    private bool ChooseLauncher()
    {
        var picker = new OpenFileDialog { Title = "Choose ComfyUI.exe or the NVIDIA startup script", Filter = "ComfyUI launcher|*.exe;*.bat;*.cmd", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return false;
        SetLauncher(picker.FileName); return true;
    }
    private void SetLauncher(string path)
    {
        var outputFolder = ComfyServerLauncher.DetectOutputFolder(path);
        Hub.Core.Settings.Update(s =>
        {
            s.ImageGeneration.LauncherPath = path;
            if (outputFolder is not null && string.IsNullOrWhiteSpace(s.ImageGeneration.OutputFolder)) s.ImageGeneration.OutputFolder = outputFolder;
        });
        Refresh();
    }
    private void OnChooseLauncher(object sender, RoutedEventArgs e) => ChooseLauncher();
    private async void OnStartServer(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = Options();
            if (string.IsNullOrWhiteSpace(Hub.S.ImageGeneration.LauncherPath))
            {
                var installed = ComfyServerLauncher.FindInstalledLauncher();
                if (installed is not null) SetLauncher(installed);
                else if (!ChooseLauncher()) return;
            }
            await Session.StartServerAsync(options);
        }
        catch (Exception ex) { Session.Message(ex.Message, true); }
    }
    private async void OnChooseOutput(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose ComfyUI's output folder" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        Hub.Core.Settings.Update(s => s.ImageGeneration.OutputFolder = picker.FolderName);
        await Session.ApplyRetentionAsync(report: true);
        Refresh();
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (Session.Selected is not { } item) return;
        var dialog = new SaveFileDialog { Title = "Save generated image", FileName = "Aqua-" + item.Created.ToString("yyyyMMdd-HHmmss") + ".png", Filter = "PNG image|*.png", DefaultExt = ".png" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await Task.Run(() => File.Copy(Session.Archive.ImagePath(item), dialog.FileName, true));
            Session.Message("Image exported.");
        }
        catch (Exception ex) { Session.Message("Couldn't save the image: " + ex.Message, true); }
    }
    private void OnOpen(object sender, RoutedEventArgs e) { if (Preview.Source is BitmapSource image) PictureViewer.Show("Aqua · Generated image", image); }
    private void OnGuide(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("https://docs.comfy.org/tutorials/flux/flux-2-klein");
    private void OnResize(object sender, SizeChangedEventArgs e)
    {
        if (FormColumn is null) return;
        var narrow = ActualWidth < 840;
        FormColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(330);
        Grid.SetColumn(PreviewColumn, narrow ? 0 : 1);
        Grid.SetRow(PreviewColumn, narrow ? 1 : 0);
        Grid.SetColumnSpan(Form, narrow ? 2 : 1);
        Grid.SetColumnSpan(PreviewColumn, narrow ? 2 : 1);
        Form.Margin = narrow ? new Thickness(0, 0, 0, 24) : new Thickness(0, 0, 24, 0);
    }
    private sealed record GalleryItem(GeneratedImage Item, ImageSource? Thumbnail);
}
