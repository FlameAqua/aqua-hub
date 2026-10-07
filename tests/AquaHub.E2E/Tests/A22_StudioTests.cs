using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

public sealed class A22_StudioTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    protected override SessionOptions Options => new()
    {
        EditSettings = root =>
        {
            root["ai"] = new JsonObject { ["enabled"] = false, ["manageOllama"] = false };
            var output = Path.Combine(E2EConfig.RunDir, "comfy-fixture", "output");
            Directory.CreateDirectory(output);
            var launcher = Path.Combine(E2EConfig.RunDir, "comfy-fixture", "run_nvidia_gpu.bat");
            File.WriteAllText(launcher, "@echo off\r\n");
            root["imageGeneration"] = new JsonObject { ["outputFolder"] = output, ["launcherPath"] = launcher };
        },
    };

    private void Tab(string mode)
    {
        var button = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-tab-" + mode), "Ask " + mode + " tab");
        Ui.SetToggle(button, true);
    }

    [Fact]
    public void T01_TabsKeepDraftsSeparateAndRejectRemoteImageServers()
    {
        Run(() =>
        {
            GoTo("ask");
            Tab("chat");
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "Chat composer"), "Keep this chat draft");
            // A real PNG fixture exercises archive loading, preview decoding, and export availability without inference.
            var gallery = Path.Combine(App.ProfileDir, "generated-images");
            Directory.CreateDirectory(gallery);
            const string id = "87e04031ff5c43a9ae68808f1c18bc55";
            File.Copy(Path.Combine(Path.GetDirectoryName(E2EConfig.ExePath)!, "Assets", "AquaHub.png"), Path.Combine(gallery, id + ".png"), true);
            File.WriteAllText(Path.Combine(gallery, id + ".png.json"), new JsonObject
            {
                ["Id"] = id, ["Prompt"] = "Studio test fixture", ["Seed"] = 42, ["Width"] = 128, ["Height"] = 128,
                ["Model"] = "flux-2-klein-base-4b-fp8.safetensors", ["Created"] = DateTimeOffset.Now.ToString("O"), ["Seconds"] = 1,
                ["Steps"] = 35, ["ComfyOutputPath"] = "fixture.png",
            }.ToJsonString());
            var serverCopy = Path.Combine(App.Settings.GetString("imageGeneration.outputFolder")!, "fixture.png");
            File.Copy(Path.Combine(gallery, id + ".png"), serverCopy, true);
            Tab("images");
            var prompt = Ui.WaitFind(PageRoot("ask"), Ui.Id("image-prompt"), "image prompt");
            Wait.For(() => Ui.WaitFind(PageRoot("ask"), Ui.Id("image-save"), "export image").Current.IsEnabled, "saved image preview loaded");
            Assert.Equal("flux-2-klein-base-4b-fp8.safetensors", Ui.SelectedComboItem(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-diffusion"), "diffusion model")));
            Assert.Equal("qwen_3_4b.safetensors", Ui.SelectedComboItem(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-encoder"), "text encoder")));
            Assert.Equal("flux2-vae.safetensors", Ui.SelectedComboItem(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-vae"), "image decoder")));
            var family = Ui.WaitFind(PageRoot("ask"), Ui.Id("image-variant"), "image model family");
            Ui.SelectComboItem(family, "Klein 4B · fast, 4 steps", Pid);
            Assert.Equal("flux-2-klein-4b-fp8.safetensors", Ui.SelectedComboItem(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-diffusion"), "fast diffusion model")));
            Ui.SelectComboItem(family, "Klein Base 4B · quality", Pid);
            Ui.SetValue(prompt, "A watercolour lighthouse");
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-endpoint"), "image endpoint"), "https://example.com");
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-check"), "check connection"));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-status"), "image status")).Contains("loopback"), "remote endpoints rejected before network access");
            Tab("roleplay");
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("roleplay-persona"), "persona"), "A friendly lighthouse keeper");
            Tab("images");
            Assert.Equal("A watercolour lighthouse", Ui.ValueOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-prompt"), "image prompt")));
            Tab("chat");
            Assert.Equal("Keep this chat draft", Ui.ValueOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "chat composer")));
            Tab("images");
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-endpoint"), "local endpoint"), "http://127.0.0.1:8188");
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-edit"), "edit image"));
            Assert.Equal("", Ui.ValueOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-prompt"), "edit instructions")));
            Assert.Equal("Apply image edit", Ui.NameOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-generate"), "apply edit")));
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-prompt"), "edit instructions"), "Change the sky to sunset");
            Shot("studio-reference-edit");
            ExpectJournal("image-generate", () => Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-generate"), "apply edit")));
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-clear-reference"), "clear reference"));
            ExpectJournal("image-generate", () => Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-regenerate"), "regenerate")));
            Assert.Equal("Studio test fixture", Ui.ValueOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-prompt"), "restored prompt")));
            ExpectJournal("image-folder", () => Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-folder"), "image folder")));
            ExpectJournal("comfy-start", () => Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-start-server"), "start ComfyUI")));
            Ui.SelectComboItem(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-retention"), "image expiry"), "7 days", Pid);
            App.Settings.WaitFor("imageGeneration.retentionDays", n => n?.GetValue<int>() == 7, "image expiry persists");
            Wait.For(() => Ui.WaitFind(PageRoot("ask"), Ui.Id("image-delete"), "delete image").Current.IsEnabled, "expiry complete");
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-delete"), "delete image"));
            Assert.True(File.Exists(Path.Combine(gallery, id + ".png"))); // First click asks; no premature deletion.
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-confirm-delete"), "confirm deletion"));
            Wait.For(() => !File.Exists(Path.Combine(gallery, id + ".png.json")), "image metadata deleted");
            Assert.False(File.Exists(Path.Combine(gallery, id + ".png")));
            Assert.False(File.Exists(serverCopy));
            Wait.For(() => !Ui.WaitFind(PageRoot("ask"), Ui.Id("image-save"), "empty preview").Current.IsEnabled, "deleted image removed from preview");
        });
    }

    [Fact]
    public void T02_ImageFormValidatesPromptAndRoleplayCanStartANewScene()
    {
        Run(() =>
        {
            GoTo("ask"); Tab("images");
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-prompt"), "prompt"), "");
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-generate"), "generate"));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("image-status"), "status")).Contains("Describe the image"), "empty prompt explanation");
            Tab("roleplay");
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("roleplay-persona"), "persona"), "A navigator");
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("roleplay-new-scene"), "new scene"));
            Assert.Equal("", Ui.ValueOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("roleplay-persona"), "cleared persona")));
            Tab("chat");
        });
    }
}
