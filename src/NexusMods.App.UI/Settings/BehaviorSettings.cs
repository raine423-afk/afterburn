using NexusMods.Sdk.Settings;

namespace NexusMods.App.UI.Settings;

public record BehaviorSettings : ISettings
{
    public bool BringWindowToFront { get; set; } = true;

    /// <summary>Afterburn: turns off the interface transitions (fades, slides, hover effects).</summary>
    public bool DisableAnimations { get; set; } = false;

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.BringWindowToFront,
            new PropertyOptions<BehaviorSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Bring app window to front",
                DescriptionFactory = _ => "When enabled, operations like adding a collection will bring the app window to the foreground",
            },
            new BooleanContainerOptions()
        ).ConfigureProperty(
            x => x.DisableAnimations,
            new PropertyOptions<BehaviorSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Turn off animations",
                DescriptionFactory = _ => "Turns off interface transitions (fades, slides and hover effects). Takes effect straight away.",
            },
            new BooleanContainerOptions()
        );
    }
}
