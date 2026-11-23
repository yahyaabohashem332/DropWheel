using Petal.App.Settings;
using Petal.App.Shell;
using Petal.Core.Util;

namespace Petal.Tests;

/// <summary>How the app offers an update by its urgency: a required one can't be skipped.</summary>
[Collection(PdfEditorWindowTests.OneApplication)]
public sealed class UpdateWindowTests
{
    private static UpdateManifest Update(string? urgency) => new()
    {
        Version = "9.1.0",
        Url = "DropWheel-Setup-9.1.0.exe",
        Sha256 = new string('a', 64),
        Size = 1000,
        Urgency = urgency,
        Changes = urgency is null ? null : ["One thing.", "Another."],
    };

    [Theory]
    [InlineData(null, true, "Skip This Version")]
    [InlineData("recommended", true, "Skip This Version")]
    [InlineData("optional", true, "Skip This Version")]
    [InlineData("required", false, "Later")]
    public void A_required_update_comes_back_even_when_skipped(string? urgency, bool skippable, string button) =>
        PdfEditorWindowTests.RunOnInterface(() =>
        {
            var kept = SettingsStore.Current.SkippedVersion;
            SettingsStore.Current.SkippedVersion = "9.1.0";
            try
            {
                var update = Update(urgency);
                Assert.Equal(skippable, UpdateService.Skipped(update));
                var window = (UpdateWindow)typeof(UpdateWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(UpdateManifest)])!
                    .Invoke([update]);
                Assert.Equal(Petal.App.Localization.L.T(button), window.SkipLabelForTest);
                window.Close();
            }
            finally
            {
                SettingsStore.Current.SkippedVersion = kept;
            }
            return Task.CompletedTask;
        });
}
