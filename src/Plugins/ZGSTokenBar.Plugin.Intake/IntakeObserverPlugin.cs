using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.Intake;

// The desktop owns the panel and its on-demand reads. Enabling this module
// registers an entry, without starting a data source or a refresh timer.
public sealed class IntakeObserverPlugin : BuiltinPluginBase
{
    public const string Id = "zgstokenbar.monitor.intake";
    public const bool DefaultEnabled = true;

    public override PluginManifest Manifest => new(1, Id, "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "intake", ["details"], DefaultEnabled, 51, []) { DisplayName = "Work intake" };

    public static MiniCardContribution Launcher { get; } =
        new("card.intake", Id, "intake", ContributionKind.Metric, 51, "intake.title", "intake.icon", "accent.intake", []);
}
