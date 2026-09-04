using Jobkeep.Modules.Documents;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// The "Documents" section of appsettings.json is a transcription of the C# defaults
/// in <see cref="DocumentOptions"/>, and this is the check that it was transcribed
/// correctly.
///
/// <para>
/// It exists because declaring those values CHANGED THE DIRECTION OF AUTHORITY.
/// Before, the numbers were compile-time constants and could not be got wrong at
/// runtime; now appsettings wins, so a dropped zero in <c>MaxBytes</c> silently
/// gives the app a 512 KB upload cap and nothing else in the suite notices — the
/// upload tests push a small file and would still pass. That failure is invisible,
/// and it lands on the one limit DocumentOptions itself calls "the app's first real
/// attack surface".
/// </para>
///
/// <para>
/// What it does NOT catch, deliberately: a misspelled KEY. A typo'd key binds
/// nothing, the property keeps its C# default, and the default is the same number
/// these assertions expect — so the test passes and the app is correct anyway. The
/// only thing worth guarding here is a wrong VALUE, which is the only thing that
/// can actually change behaviour.
/// </para>
/// </summary>
public class DocumentOptionsConfigurationTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public void Appsettings_transcribes_the_defaults_it_is_meant_to_mirror()
    {
        var options = Fixture.App.Services.GetRequiredService<DocumentOptions>();

        Assert.Equal(5 * 1024 * 1024, options.MaxBytes);
        Assert.Equal(40, options.MinTextChars);
        Assert.Equal(24000, options.MaxStructureChars);
        Assert.Equal(64 * 1024 * 1024, options.MaxDecompressedBytes);
        Assert.Equal(200, options.MaxListSize);
    }

    /// <summary>
    /// ParseInBackground is the one DocumentOptions property deliberately kept OUT of
    /// appsettings.json — it is a test seam, and a knob in a config file is a knob
    /// somebody turns. This asserts the seam still works through UseSetting alone,
    /// which is the property that would break if a well-meaning change "completed"
    /// the section by adding the missing key.
    /// </summary>
    [Fact]
    public void Background_parsing_is_off_under_test_and_owes_that_to_no_config_file()
    {
        var options = Fixture.App.Services.GetRequiredService<DocumentOptions>();

        Assert.False(options.ParseInBackground);
    }
}
