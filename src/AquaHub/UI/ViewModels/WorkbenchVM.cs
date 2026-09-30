using System.Windows.Input;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.UI.ViewModels;

/// <summary>A taught skill in the Workbench list.</summary>
public sealed class SkillVM : ObservableObject
{
    public SkillVM(AskSkill skill) => Skill = skill;

    public AskSkill Skill { get; }
    public string Id => Skill.Id;
    public string Name => Skill.Name;
    public string Description => Skill.Description;
    public string Triggers => Skill.Triggers.Count == 0 ? "" : "“" + string.Join("” · “", Skill.Triggers.Take(4)) + "”";
    public bool HasTriggers => Skill.Triggers.Count > 0;
    public string Detail
    {
        get
        {
            var parts = new List<string> { Skill.Steps.Count == 0 ? "instructions only" : Plural.Of(Skill.Steps.Count, "step") };
            parts.Add(Skill.Uses == 0 ? "not used yet" : $"used {Plural.Of(Skill.Uses, "time")}" + (Skill.LastUsed is { } last ? ", last " + TimeText.AgoPhrase(last) : ""));
            return string.Join(" · ", parts);
        }
    }

    public bool Enabled
    {
        get => Skill.Enabled;
        set
        {
            if (Skill.Enabled == value) return;
            Skill.Enabled = value;
            Hub.Core.Workbench.SetEnabled(Skill.Id, value);
            Raise();
        }
    }

    public ICommand? TryCommand { get; set; }
    public ICommand? EditCommand { get; set; }
    public ICommand? DeleteCommand { get; set; }
}

/// <summary>One step of the skill being edited ("Open app · name = Spotify").</summary>
public sealed class StepRowVM
{
    public int Number { get; init; }
    public string Text { get; init; } = "";
    public string Icon { get; init; } = "bolt";
    public ICommand? RemoveCommand { get; set; }
}

/// <summary>Something Aqua remembers.</summary>
public sealed class MemoryVM : ObservableObject
{
    private bool _editing;
    private string _editText = "";

    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public ICommand? EditCommand { get; set; }
    public ICommand? SaveCommand { get; set; }
    public ICommand? CancelCommand { get; set; }
    public ICommand? DeleteCommand { get; set; }
}

/// <summary>A group of Ask's tools in "What Aqua can do".</summary>
public sealed class ToolGroupVM
{
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public string Icon { get; init; } = "sparkle";
    public List<ToolRowVM> Tools { get; init; } = new();
}

public sealed class ToolRowVM
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "bolt";
    public string Badge { get; init; } = "";
    public bool HasBadge => Badge.Length > 0;
}
