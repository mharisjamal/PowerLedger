using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public class RecoveryCodeViewModelTests
{
    [Fact]
    public void Copy_copies_the_code_to_the_clipboard()
    {
        string? copied = null;
        var model = new RecoveryCodeViewModel("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE", new FakeSaver(), text => copied = text);

        model.Copy.Execute(null);

        copied.ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
    }

    [Fact]
    public void Save_as_text_file_writes_the_code_where_the_user_says()
    {
        using var saver = new FakeSaver();
        var model = new RecoveryCodeViewModel("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE", saver, _ => { });

        model.SaveAsTextFile.Execute(null);

        saver.Suggested.ShouldBe("PowerLedger-recovery-code.txt");
        File.ReadAllText(saver.Chosen).ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
    }

    [Fact]
    public void A_cancelled_save_writes_nothing()
    {
        using var saver = new FakeSaver { Cancel = true };
        var model = new RecoveryCodeViewModel("THE-CODE", saver, _ => { });

        model.SaveAsTextFile.Execute(null);

        File.Exists(saver.Chosen).ShouldBeFalse();
    }
}
