using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// Builder Phase 2: named pipeline profiles — the persisted store, app-wide activation through
/// StudioServices.Reconfigure, apply-at-boot, the shell's profile bar, and saving from the Builder.
/// </summary>
public class PipelineProfileStoreTests
{
    private static PipelineProfileStore Store() => new(Path.Combine(TestSupport.TempDir(), "voxa-pipelines.json"));

    [Fact]
    public void Save_List_Activate_Roundtrips_Across_Instances()
    {
        var path = Path.Combine(TestSupport.TempDir(), "p.json");
        var a = new PipelineProfileStore(path);
        a.Save("Local", new Dictionary<string, string?> { ["Voxa:Stt"] = "WhisperCpp", ["Voxa:Tts"] = "Piper" });
        a.SetActive("Local");

        var b = new PipelineProfileStore(path); // a fresh instance reads the same file
        Assert.Contains("Local", b.Names);
        Assert.Equal("Local", b.ActiveName);
        Assert.True(b.TryGetActive(out var pairs));
        Assert.Equal("WhisperCpp", pairs["Voxa:Stt"]);
    }

    [Fact]
    public void Secrets_Are_Never_Persisted()
    {
        var store = Store();
        store.Save("Cloud", new Dictionary<string, string?>
        {
            ["Voxa:Agent:Provider"] = "OpenAI",
            ["Voxa:Agent:ApiKey"] = "sk-secret",
        });
        Assert.True(store.TryGet("Cloud", out var pairs));
        Assert.Equal("OpenAI", pairs["Voxa:Agent:Provider"]);
        Assert.False(pairs.ContainsKey("Voxa:Agent:ApiKey")); // keys live in the DPAPI secrets layer only
    }

    [Fact]
    public void Deleting_The_Active_Profile_Clears_Active()
    {
        var store = Store();
        store.Save("X", new Dictionary<string, string?> { ["Voxa:Tts"] = "Kokoro" });
        store.SetActive("X");
        store.Delete("X");
        Assert.DoesNotContain("X", store.Names);
        Assert.Null(store.ActiveName);
    }

    [Fact]
    public void Missing_File_Yields_Empty_And_Never_Throws()
    {
        var store = new PipelineProfileStore(Path.Combine(TestSupport.TempDir(), "nope.json"));
        Assert.Empty(store.Names);
        Assert.Null(store.ActiveName);
        Assert.False(store.TryGetActive(out _));
    }
}

public class PipelineProfileIntegrationTests
{
    private static PipelineProfileStore SeededStore(string name, Dictionary<string, string?> pairs, bool active = false)
    {
        var store = new PipelineProfileStore(Path.Combine(TestSupport.TempDir(), "p.json"));
        store.Save(name, pairs);
        if (active) store.SetActive(name);
        return store;
    }

    [Fact]
    public void Activating_A_Profile_Reconfigures_The_Live_Container()
    {
        var profiles = SeededStore("Kokoro setup", new Dictionary<string, string?>
        {
            ["Voxa:Tts"] = "Kokoro",
            ["Voxa:Kokoro:Voice"] = "af_bella",
        });
        var services = TestSupport.Services(profiles: profiles);

        services.ActivateProfile("Kokoro setup");

        Assert.Equal("Kokoro", services.Configuration["Voxa:Tts"]);
        Assert.Equal("af_bella", services.Configuration["Voxa:Kokoro:Voice"]);
        Assert.Equal("Kokoro setup", services.Profiles.ActiveName);
    }

    [Fact]
    public void A_Saved_Active_Profile_Applies_At_Boot()
    {
        var path = Path.Combine(TestSupport.TempDir(), "p.json");
        var seed = new PipelineProfileStore(path);
        seed.Save("Kokoro", new Dictionary<string, string?> { ["Voxa:Tts"] = "Kokoro" });
        seed.SetActive("Kokoro");

        // A fresh store over the same file → StudioServices folds the active profile into the first build.
        var services = TestSupport.Services(profiles: new PipelineProfileStore(path));
        Assert.Equal("Kokoro", services.Configuration["Voxa:Tts"]);
    }

    [Fact]
    public void The_Shell_Lists_Profiles_And_Selecting_One_Activates_It_App_Wide()
    {
        var profiles = SeededStore("Kokoro", new Dictionary<string, string?> { ["Voxa:Tts"] = "Kokoro" });
        var services = TestSupport.Services(profiles: profiles);
        var shell = new MainWindowViewModel(services);

        Assert.Contains("Kokoro", shell.PipelineProfiles);
        Assert.Equal(MainWindowViewModel.CustomProfile, shell.SelectedPipelineProfile); // nothing active yet

        shell.SelectedPipelineProfile = "Kokoro"; // the user picks it from the bar

        Assert.Equal("Kokoro", services.Configuration["Voxa:Tts"]); // every page now composes from it
        Assert.Equal("Kokoro", services.Profiles.ActiveName);
    }

    [Fact]
    public void A_Config_Apply_Marks_The_Profile_Bar_Custom()
    {
        var profiles = SeededStore("Kokoro", new Dictionary<string, string?> { ["Voxa:Tts"] = "Kokoro" });
        var services = TestSupport.Services(profiles: profiles);
        var shell = new MainWindowViewModel(services);
        shell.SelectedPipelineProfile = "Kokoro";
        Assert.Equal("Kokoro", shell.SelectedPipelineProfile);

        // A raw Config Apply diverges from the saved profile → the bar reads Custom.
        shell.Config.SelectedTts = "Piper";
        shell.Config.ApplyCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.CustomProfile, shell.SelectedPipelineProfile);
        Assert.Null(services.Profiles.ActiveName);
    }

    [Fact]
    public void Builder_Save_As_Profile_Stores_And_Activates_The_Default_Shape()
    {
        var services = TestSupport.Services();
        var builder = new BuilderViewModel(services);
        builder.ProfileName = "My Local";

        Assert.True(builder.SaveAsProfileCommand.CanExecute(null)); // the seeded default chain is valid + default-shape
        builder.SaveAsProfileCommand.Execute(null);

        Assert.Contains("My Local", services.Profiles.Names);
        Assert.Equal("My Local", services.Profiles.ActiveName);
        Assert.Equal("", builder.ProfileName); // cleared after the save
    }

    [Fact]
    public void Builder_Cannot_Save_A_Custom_Shape_As_A_Profile()
    {
        var services = TestSupport.Services();
        var builder = new BuilderViewModel(services);
        builder.ProfileName = "Custom";

        // Drop the filter and wire STT straight to the agent: valid, but not a config-expressible shape.
        builder.Select(builder.Nodes.First(n => n.Kind == BuilderNodeKind.Filter));
        builder.RemoveSelectedCommand.Execute(null);
        Assert.True(builder.TryConnect(
            builder.Nodes.First(n => n.Kind == BuilderNodeKind.Stt),
            builder.Nodes.First(n => n.Kind == BuilderNodeKind.Agent), out _));

        Assert.True(builder.IsChainValid);
        Assert.False(builder.IsDefaultShape);
        Assert.False(builder.SaveAsProfileCommand.CanExecute(null)); // custom shapes export as C#, not profiles
    }

    [Fact]
    public void Builder_Save_As_Profile_Is_Disabled_While_A_Session_Is_Live()
    {
        // Codex P2: saving both saves AND activates (a rebuild), which can't run under a live session —
        // so the command is disabled rather than saving without applying and leaving the store stale.
        var services = TestSupport.Services();
        var builder = new BuilderViewModel(services);
        builder.ProfileName = "X";
        Assert.True(builder.SaveAsProfileCommand.CanExecute(null));

        builder.RunBlocked = true; // a Talk/Metrics run owns the audio device
        Assert.False(builder.SaveAsProfileCommand.CanExecute(null));

        builder.RunBlocked = false;
        Assert.True(builder.SaveAsProfileCommand.CanExecute(null));
    }

    [Fact]
    public void Builder_Save_Updates_The_Active_Profile_In_Place()
    {
        var services = TestSupport.Services();
        var builder = new BuilderViewModel(services);

        // Create + activate "Local" via Save-as-new.
        builder.ProfileName = "Local";
        builder.SaveAsProfileCommand.Execute(null);
        Assert.Equal("Local", services.Profiles.ActiveName);
        Assert.True(builder.HasActiveProfile);

        // Now "Save" (no name) updates that same profile with a changed canvas.
        Assert.True(builder.SaveCurrentProfileCommand.CanExecute(null));
        builder.Nodes.First(n => n.Kind == BuilderNodeKind.Stt).Model.Options["Model"] = "base.en";
        builder.SaveCurrentProfileCommand.Execute(null);

        Assert.True(services.Profiles.TryGet("Local", out var pairs));
        Assert.Equal("base.en", pairs["Voxa:WhisperCpp:Model"]);                  // saved into the same profile
        Assert.Equal("base.en", services.Configuration["Voxa:WhisperCpp:Model"]); // and re-applied live
        Assert.Single(services.Profiles.Names);                                    // no new profile created
    }

    [Fact]
    public void Builder_Save_Is_Disabled_With_No_Active_Profile()
    {
        var services = TestSupport.Services();
        var builder = new BuilderViewModel(services);
        Assert.Null(services.Profiles.ActiveName);
        Assert.False(builder.HasActiveProfile);
        Assert.False(builder.SaveCurrentProfileCommand.CanExecute(null)); // nothing to save into yet
    }

    [Fact]
    public void Selecting_A_Profile_Loads_It_Onto_The_Builder_Canvas()
    {
        var profiles = SeededStore("KokoroSetup", new Dictionary<string, string?>
        {
            ["Voxa:Tts"] = "Kokoro",
            ["Voxa:Kokoro:Voice"] = "af_bella",
        });
        var services = TestSupport.Services(profiles: profiles);
        var shell = new MainWindowViewModel(services);

        shell.SelectedPipelineProfile = "KokoroSetup"; // pick it in the bar

        var tts = shell.Builder.Nodes.First(n => n.Kind == BuilderNodeKind.Tts);
        Assert.Equal("Kokoro", tts.Model.Provider);          // the canvas now reflects the activated profile
        Assert.Equal("af_bella", tts.Model.Options["Voice"]);
    }
}
