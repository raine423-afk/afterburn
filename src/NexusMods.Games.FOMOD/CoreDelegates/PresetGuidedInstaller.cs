using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Games.FOMOD.CoreDelegates;

/// <summary>
/// A IGuidedInstaller implementation that uses a preset list of steps to make the same choices
/// a user previously made for specific steps.
/// </summary>
public class PresetGuidedInstaller : IGuidedInstaller
{
    private readonly FomodOption[] _steps;
    private int _currentStep = 0;

    /// <summary>
    /// Constructor.
    /// </summary>
    public PresetGuidedInstaller(FomodOption[] steps)
    {
        _steps = steps;
    }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public void SetupInstaller(string name) { }

    /// <inheritdoc/>
    public void CleanupInstaller() { }

    /// <inheritdoc/>
    public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
    {
        // Afterburn: a mod can be updated after a collection saved its installer choices, so the installer may have
        // more or different steps than the preset. Match the step by name first, then by position, and if there's
        // no saved choice for this step, use the installer's own defaults instead of crashing.
        var step = _steps.FirstOrDefault(s => string.Equals(s.name, installationStep.Name, StringComparison.Ordinal))
                   ?? (_currentStep < _steps.Length ? _steps[_currentStep] : null);
        _currentStep++;

        var choices = step is null
            ? new List<SelectedOption>()
            : (from srcGroup in step.groups
               from installGroup in installationStep.Groups
               where installGroup.Name == srcGroup.name
               from srcChoice in srcGroup.choices
               from installChoice in installGroup.Options
               where installChoice.Name == srcChoice.name && installChoice.Type != OptionType.Disabled
               select new SelectedOption(installGroup.Id, installChoice.Id)).ToList();

        // Fill in any group the preset didn't cover with the installer's defaults.
        foreach (var group in installationStep.Groups)
        {
            if (choices.Any(c => c.GroupId == group.Id)) continue;

            var defaults = group.Options
                .Where(o => o.Type is OptionType.Required or OptionType.PreSelected)
                .Select(o => new SelectedOption(group.Id, o.Id))
                .ToList();

            if (defaults.Count == 0 && (group.Type is OptionGroupType.ExactlyOne or OptionGroupType.AtLeastOne))
            {
                var first = group.Options.FirstOrDefault(o => o.Type != OptionType.Disabled);
                if (first is not null) defaults.Add(new SelectedOption(group.Id, first.Id));
            }

            if ((group.Type is OptionGroupType.ExactlyOne or OptionGroupType.AtMostOne) && defaults.Count > 1)
                defaults = defaults.Take(1).ToList();

            choices.AddRange(defaults);
        }

        return Task.FromResult(new UserChoice(new UserChoice.GoToNextStep(choices.ToArray())));
    }
}
