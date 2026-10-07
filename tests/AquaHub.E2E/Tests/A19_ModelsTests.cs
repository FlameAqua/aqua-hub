using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>Phase 4 UI contracts against a tiny synthetic GGUF. No inference or real downloads are needed.</summary>
public sealed class A19_ModelsTests : E2ETestBase
{
    public A19_ModelsTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    private const string ModelId = "hf:aqua/qa/Aqua-QA-Q4_K_M.gguf";
    private const string ModelName = "Aqua QA · Q4_K_M";

    protected override SessionOptions Options => new()
    {
        EditSettings = root =>
        {
            root["onboardingComplete"] = true;
            root["ai"] = new JsonObject
            {
                ["enabled"] = false, ["provider"] = "ollama", ["model"] = "auto", ["manageOllama"] = false,
                ["llama"] = new JsonObject { ["modelsFolder"] = SeedModel(), ["useOllamaModels"] = false },
            };
        },
    };

    private static string SeedModel()
    {
        var folder = Path.Combine(E2EConfig.RunDir, "model-fixture");
        Directory.CreateDirectory(folder);
        const string filename = "Aqua-QA-Q4_K_M.gguf";
        var path = Path.Combine(folder, filename);
        using (var stream = File.Create(path))
        using (var w = new BinaryWriter(stream))
        {
            void S(string value) { var b = Encoding.UTF8.GetBytes(value); w.Write((ulong)b.Length); w.Write(b); }
            void Text(string key, string value) { S(key); w.Write(8u); S(value); }
            w.Write(0x46554747u); w.Write(3u); w.Write(0ul); w.Write(4ul);
            Text("general.architecture", "llama"); Text("general.name", "Aqua QA");
            Text("tokenizer.chat_template", "{% if enable_thinking %}think{% endif %} tools");
            S("llama.context_length"); w.Write(4u); w.Write(32768u);
        }
        var size = new FileInfo(path).Length;
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(folder, "library.json"), JsonSerializer.Serialize(new
        {
            version = 1, models = new[] { new { repo = "aqua/qa", revision = new string('a', 40), file = filename,
                path = filename, size, sha256 = sha, added = DateTimeOffset.UtcNow } },
        }));
        return folder;
    }

    private AutomationElement Models => PageRoot("models");
    private AutomationElement Field(string id) => Ui.WaitFind(Main, Ui.Id(id), id);

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_LibraryRolesPersistAndDownloadsAreIntercepted() => Run(() =>
    {
        GoTo("models");
        Wait.For(() => Ui.Texts(Models).Contains(ModelName), "synthetic model in library");
        foreach (var role in new[] { "main", "creative", "background" })
        {
            Ui.SelectComboItem(Field("models-role-" + role), ModelName, Pid);
            App.Settings.WaitForString(role == "main" ? "ai.model" : "ai.llama." + role + "Model", ModelId);
        }
        GoTo("today"); GoTo("models");
        File.WriteAllText(Path.Combine(E2EConfig.RunDir, "models-ui.txt"), TreeSnapshot.Dump(TreeSnapshot.Capture(Models)));
        Wait.For(() => Ui.Find(Models, Ui.Name("Thinking")) is not null, "model capability available to accessibility");
        Ui.SelectComboItem(Field("models-role-main"), "Automatic", Pid);
        App.Settings.WaitFor("ai.model", n => n?.GetValue<string>() is "" or "auto", "automatic main model");
        Ui.SelectComboItem(Field("models-role-background"), "Use Main (recommended)", Pid);
        App.Settings.WaitForString("ai.llama.backgroundModel", "");
        var suggested = Field("models-suggested");
        var download = Ui.FindAll(suggested, Ui.Type(ControlType.Button)).First(b => b.Current.IsEnabled);
        ExpectJournal("model-download", () => Ui.Invoke(download));
        Expect(!Directory.EnumerateFiles(App.ProfileDir, "*.part", SearchOption.AllDirectories).Any(), "dry-run download wrote no partial files");
        Shot("models-roles");
    });

    [Fact]
    public void T02_MissingRuntimeDoesNotSwitchProviderAndInstallIsIntercepted() => Run(() =>
    {
        GoTo("models");
        Ui.Invoke(Ui.WaitFind(Models, Ui.Name("AI settings"), "AI settings link"));
        PageRoot("settings");
        Ui.SelectComboItem(Field("settings-ai-provider"), "Aqua's own runtime (faster, more control)", Pid);
        Expect(App.Settings.GetString("ai.provider") == "ollama", "missing runtime leaves existing provider active");
        ExpectJournal("llama-install", () => Ui.Invoke(Field("settings-ai-runtime-install")));
        Expect(App.Settings.GetString("ai.provider") == "ollama", "dry-run install doesn't activate a missing executable");
        Ui.Expand(Field("settings-ai-advanced"));
        Ui.SelectComboItem(Field("settings-ai-cache"), "f16 · Exact, twice the memory", Pid);
        App.Settings.WaitForString("ai.llama.cacheType", "f16");
        Ui.SetValue(Field("settings-ai-context"), "16384");
        App.Settings.WaitFor("ai.llama.contextTokens", n => n?.GetValue<int>() == 16384, "16K context persisted");
        Shot("runtime-settings");
    });

    [Fact]
    public void T03_ExternalProviderRoundTripKeepsSelection() => Run(() =>
    {
        GoTo("models"); Ui.Invoke(Ui.WaitFind(Models, Ui.Name("AI settings"), "AI settings link"));
        Ui.SelectComboItem(Field("settings-ai-provider"), "OpenAI-compatible server", Pid);
        App.Settings.WaitForString("ai.provider", "openai");
        Wait.For(() => Field("settings-ai-provider").Current.IsEnabled, "provider health check finished");
        GoTo("today"); GoTo("models"); Ui.Invoke(Ui.WaitFind(Models, Ui.Name("AI settings"), "AI settings link"));
        Ui.SelectComboItem(Field("settings-ai-provider"), "Ollama", Pid);
        App.Settings.WaitForString("ai.provider", "ollama");
    });
}
