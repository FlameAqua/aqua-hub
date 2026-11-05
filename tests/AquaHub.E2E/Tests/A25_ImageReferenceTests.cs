using System.Security.Cryptography;
using AquaHub.Core.Ai.Imaging;

namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Images")]
public sealed class A25_ImageReferenceTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    private const string ImageId = "47199677f180465eac6d27bad8983557";
    protected override SessionOptions Options => new()
    {
        Offline = true,
        SeedProfile = profile =>
        {
            var archive = new ImageArchive(Path.Combine(profile, "generated-images"));
            var png = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(E2EConfig.ExePath)!, "Assets", "AquaHub.png"));
            var image = new GeneratedImage(ImageId, "Original gallery fixture", 42, 1024, 1024,
                "flux-2-klein-base-4b-fp8.safetensors", DateTimeOffset.Now, 1)
            { EditedFrom = new ImageReference(new string('a', 32), "Missing original", 1024, 1024) };
            archive.SaveAsync(image, png).GetAwaiter().GetResult();
        },
    };
    private AutomationElement Field(string id) => Ui.WaitFind(PageRoot("ask"), Ui.Id(id), id);
    private string Status => Ui.NameOf(Field("image-status"));
    private void OpenImages()
    {
        GoTo("ask"); Ui.SetToggle(Field("ask-tab-images"), true);
        Wait.For(() => Field("image-edit").Current.IsEnabled, "gallery fixture loaded");
    }
    private void Pick(string path)
    {
        Ui.Invoke(Field("image-attach"));
        var dialog = Wait.For(() => AppWindows.WithOwned(Pid).FirstOrDefault(w => Ui.NameOf(w) == "Choose an image to edit"), "image picker");
        var filename = Ui.WaitFind(dialog, Ui.And(Ui.Type(ControlType.Edit), Ui.Name("File name:")), "picker filename");
        Ui.SetValue(filename, path);
        var open = Ui.WaitFind(dialog, Ui.Name("Open"), "picker Open");
        if (Ui.Supports(open, InvokePattern.Pattern)) Ui.Invoke(open);
        else
        {
            // Some Windows shell versions expose the split Open control as a Pane. The listed file's
            // Invoke pattern performs the same native dialog acceptance, without keyboard/mouse input.
            Assert.Equal(App.ProfileDir, Path.GetDirectoryName(path));
            var file = Ui.WaitFind(dialog, Ui.And(Ui.Type(ControlType.ListItem), Ui.Name(Path.GetFileName(path))), "owned picker file");
            ((InvokePattern)file.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }
        Wait.For(() => !Ui.Exists(dialog), "picker closed");
    }

    [Fact]
    public void T01_MissingReferenceRefusesRegenerationButGalleryEditPreservesOriginal() => Run(() =>
    {
        OpenImages();
        var original = Path.Combine(App.ProfileDir, "generated-images", ImageId + ".png");
        var digest = SHA256.HashData(File.ReadAllBytes(original));
        var mark = App.Journal.Mark();
        Ui.Invoke(Field("image-regenerate"));
        Wait.For(() => Status.Contains("no longer has the image this one was edited from"), "missing-reference explanation");
        Assert.Null(App.Journal.Find(mark, "image-generate"));
        Ui.Invoke(Field("image-edit"));
        Wait.For(() => Status.Contains("Aqua edits a copy"), "gallery copy prepared");
        Assert.Equal("Apply image edit", Ui.NameOf(Field("image-generate")));
        Assert.Equal(digest, SHA256.HashData(File.ReadAllBytes(original)));
        var references = Path.Combine(App.ProfileDir, "generated-images", "references");
        Assert.Single(Directory.GetFiles(references, "*.png"));
        Ui.Invoke(Field("image-clear-reference"));
        Assert.Equal("Generate image", Ui.NameOf(Field("image-generate")));
    });

    [Fact]
    public void T02_PickerAcceptsOwnedPngAndCorruptReplacementKeepsReference() => Run(() =>
    {
        OpenImages();
        var source = Path.Combine(App.ProfileDir, "picker-source.png");
        File.Copy(Path.Combine(App.ProfileDir, "generated-images", ImageId + ".png"), source, true);
        var digest = SHA256.HashData(File.ReadAllBytes(source));
        Pick(source);
        Wait.For(() => Status.StartsWith("Added picker-source.png"), "picker source prepared");
        Assert.Equal(digest, SHA256.HashData(File.ReadAllBytes(source)));
        var kept = Directory.GetFiles(Path.Combine(App.ProfileDir, "generated-images", "references"), "*.png");
        Assert.Single(kept);
        var corrupt = Path.Combine(App.ProfileDir, "corrupt.png");
        File.WriteAllText(corrupt, "This is deliberately not an image.");
        Pick(corrupt);
        Wait.For(() => !Status.StartsWith("Preparing") && (Status.Contains("Couldn't") || Status.Contains("couldn't")), "corrupt image rejected");
        Assert.Equal("Apply image edit", Ui.NameOf(Field("image-generate")));
        Assert.True(File.Exists(kept[0]));
        Assert.Equal(digest, SHA256.HashData(File.ReadAllBytes(source)));
        Ui.Invoke(Field("image-clear-reference"));
    });
}
