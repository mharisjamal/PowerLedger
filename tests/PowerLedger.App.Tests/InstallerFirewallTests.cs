using System.IO;
using System.Text.RegularExpressions;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Households design §3: the installer's firewall rules for the service's listener, read from installer/PowerLedger.iss.
/// One rule for Private networks, and a second for Public ones, both inbound TCP to the service's program from the local
/// subnet only; every install adds both, an upgrade included, and uninstalling removes both.</summary>
public class InstallerFirewallTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(Root(), "installer", "PowerLedger.iss"));

    [Theory]
    [InlineData("PowerLedger households", "private")]
    [InlineData("PowerLedger households (public)", "public")]
    public void Each_rule_lets_in_tcp_to_the_service_from_the_local_subnet_on_its_own_profile(string name, string profile)
    {
        var add = Procedure("AddFirewallRule");

        var rule = Regex.Match(add, $"add rule name=\"{Regex.Escape(name)}\"[^;]*;", RegexOptions.Singleline);
        rule.Success.ShouldBeTrue($"AddFirewallRule adds no rule named {name}");
        rule.Value.ShouldContain("dir=in action=allow program=\"' + ServiceExecutable");
        rule.Value.ShouldContain($"protocol=TCP profile={profile} remoteip=localsubnet");
        var delete = add.IndexOf($"delete rule name=\"{name}\"", StringComparison.Ordinal);
        delete.ShouldBeInRange(0, rule.Index);                                   // replaced first, so an upgrade never doubles it
    }

    [Theory]
    [InlineData("PowerLedger households")]
    [InlineData("PowerLedger households (public)")]
    public void Uninstalling_removes_each_rule(string name)
    {
        Procedure("RemoveFirewallRule").ShouldContain($"delete rule name=\"{name}\"");
        Procedure("CurUninstallStepChanged").ShouldContain("RemoveFirewallRule;");
    }

    [Fact]
    public void Every_install_adds_the_rules_an_upgrade_included()
    {
        var step = Procedure("CurStepChanged");
        var postInstall = Regex.Match(step, @"if CurStep = ssPostInstall then\s*begin(?<body>.*?)\bend;", RegexOptions.Singleline);

        postInstall.Success.ShouldBeTrue();
        postInstall.Groups["body"].Value.ShouldContain("AddFirewallRule;");
        postInstall.Groups["body"].Value.ShouldNotContain("if ");                // nothing skips it for an upgrade
    }

    /// <summary>A Pascal procedure's text, from its heading to the next procedure or function.</summary>
    private static string Procedure(string name)
    {
        var match = Regex.Match(Script, $@"procedure {name}\b.*?(?=\r?\n(procedure|function) |\z)", RegexOptions.Singleline);
        match.Success.ShouldBeTrue($"The installer has no procedure {name}");
        return match.Value;
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerLedger.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}
