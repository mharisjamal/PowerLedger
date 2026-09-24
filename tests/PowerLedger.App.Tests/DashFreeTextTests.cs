using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Owner's round (0.8.0): no em dash or en dash reaches the screen. Every .xaml under src/, outside its comments,
/// and every string or character literal in src/'s C#, written out or escaped, is free of both; comments are left alone.</summary>
public class DashFreeTextTests
{
    [Fact]
    public void No_xaml_under_src_shows_an_em_or_en_dash()
    {
        var hits = Sources("*.xaml").SelectMany(file => DashScan.InXaml(File.ReadAllText(file)).Select(line => $"{Relative(file)}:{line}")).ToList();
        hits.ShouldBeEmpty(string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void No_string_in_src_holds_an_em_or_en_dash()
    {
        var hits = Sources("*.cs").SelectMany(file => DashScan.InCSharp(File.ReadAllText(file)).Select(line => $"{Relative(file)}:{line}")).ToList();
        hits.ShouldBeEmpty(string.Join(Environment.NewLine, hits));
    }

    [Theory]
    [InlineData("var a = \"x \u2014 y\";", 1)]
    [InlineData("var a = \"x \u2013 y\";", 1)]
    [InlineData("var a = \"x \\u2014 y\";", 1)]
    [InlineData("var a = \"x \\x2013 y\";", 1)]
    [InlineData("var a = '\u2014';", 1)]
    [InlineData("var a = '\\u2013';", 1)]
    [InlineData("var a = @\"x \"\" \u2014 y\";", 1)]
    [InlineData("var a = $\"{b} \u2014 {c}\";", 1)]
    [InlineData("var a = $\"{(b ? \"x\" : \"y\")} \u2014\";", 1)]
    [InlineData("var a = $\"{b ?? \"\u2014\"}\";", 1)]
    [InlineData("var a = $@\"{b}\n\u2014\";", 2)]
    [InlineData("var a = \"\"\"\n  x \" \u2014\n  \"\"\";", 2)]
    [InlineData("var a = $$\"\"\"{{b}} { \u2014 }\"\"\";", 1)]
    [InlineData("var a = \"\";\nvar b = \"\u2013\";", 2)]
    public void The_scan_finds_a_dash_in_any_kind_of_literal(string source, int line)
        => DashScan.InCSharp(source).ShouldBe(new[] { line });

    [Theory]
    [InlineData("// x \u2014 y\nvar a = 1;")]
    [InlineData("/// <summary>x \u2014 y</summary>")]
    [InlineData("/* x \u2014 \"y\" */ var a = \"z\";")]
    [InlineData("var a = \"x - y\"; // \u2014")]
    [InlineData("var a = $\"{b}\"; /* \u2013 */")]
    [InlineData("var a = \"//\"; var b = 1 \u2014 2;")]
    [InlineData("var a = '\"'; // \u2014")]
    [InlineData("var a = @\"\\\"; // \u2014")]
    [InlineData("var a = \"\\\"\"; // \u2014")]
    public void The_scan_leaves_comments_and_code_alone(string source)
        => DashScan.InCSharp(source).ShouldBeEmpty();

    [Theory]
    [InlineData("<TextBlock Text=\"a \u2014 b\" />", 1)]
    [InlineData("<!-- a -->\n<TextBlock Text=\"a &#x2014; b\" />", 2)]
    [InlineData("<TextBlock Text=\"a &#8211; b\" />", 1)]
    public void The_xaml_scan_finds_a_dash_written_or_as_an_entity(string xaml, int line)
        => DashScan.InXaml(xaml).ShouldBe(new[] { line });

    [Fact]
    public void The_xaml_scan_leaves_comments_alone()
        => DashScan.InXaml("<!-- a \u2014\n b -->\n<TextBlock Text=\"a - b\" />").ShouldBeEmpty();

    private static IEnumerable<string> Sources(string pattern)
        => Directory.EnumerateFiles(Path.Combine(Root(), "src"), pattern, SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Order(StringComparer.Ordinal);

    private static string Relative(string file) => Path.GetRelativePath(Root(), file).Replace('\\', '/');

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerLedger.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}

/// <summary>Finds the lines where an em dash (U+2014) or an en dash (U+2013) stands in text a user could see: in XAML
/// outside comments, written or as a character reference, and in C# inside string and character literals (regular,
/// verbatim, interpolated, raw), written or escaped, with comments and code skipped.</summary>
internal static class DashScan
{
    private const char Em = '\u2014';
    private const char En = '\u2013';

    public static IReadOnlyList<int> InXaml(string xaml)
    {
        // Comments go, their line breaks kept so the line numbers still match the file.
        var text = Regex.Replace(xaml, "<!--.*?-->", comment => new string('\n', comment.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var lines = text.Split('\n');
        return [.. Enumerable.Range(0, lines.Length)
            .Where(i => lines[i].IndexOfAny([Em, En]) >= 0 || Regex.IsMatch(lines[i], "&#(x201[34]|821[12]);|&[mn]dash;", RegexOptions.IgnoreCase))
            .Select(i => i + 1)];
    }

    public static IReadOnlyList<int> InCSharp(string source) => new CSharp(source).Run();

    private sealed class CSharp(string s)
    {
        private readonly SortedSet<int> _hits = [];
        private int _i;

        public IReadOnlyList<int> Run()
        {
            Code(inHole: false);
            return [.. _hits];
        }

        private char At(int offset = 0) => _i + offset < s.Length ? s[_i + offset] : '\0';

        private int LineAt(int index) => s.AsSpan(0, Math.Min(index, s.Length)).Count('\n') + 1;

        private void Hit(int index) => _hits.Add(LineAt(index));

        /// <summary>Code up to the end, or, in an interpolation hole, up to the brace that closes it.</summary>
        private void Code(bool inHole)
        {
            var depth = 0;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '/' && At(1) == '/')
                {
                    while (_i < s.Length && s[_i] != '\n') _i++;
                }
                else if (c == '/' && At(1) == '*')
                {
                    var end = s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? s.Length : end + 2;
                }
                else if (c == '\'')
                {
                    CharLiteral();
                }
                else if (c == '"' || (c is '$' or '@' && StartsString()))
                {
                    StringLiteral();
                }
                else
                {
                    if (c == '{') depth++;
                    else if (c == '}' && depth-- == 0 && inHole) return;
                    _i++;
                }
            }
        }

        private bool StartsString()
        {
            var j = _i;
            while (j < s.Length && s[j] is '$' or '@') j++;
            return j < s.Length && s[j] == '"';
        }

        private void CharLiteral()
        {
            _i++;
            if (At() == '\\') Escape();
            else
            {
                if (At() is Em or En) Hit(_i);
                _i++;
            }
            while (_i < s.Length && s[_i] != '\'' && s[_i] != '\n') _i++;
            _i++;
        }

        private void StringLiteral()
        {
            var dollars = 0;
            var verbatim = false;
            while (At() is '$' or '@')
            {
                if (At() == '$') dollars++;
                else verbatim = true;
                _i++;
            }
            if (!verbatim)
            {
                var quotes = 0;
                while (At(quotes) == '"') quotes++;
                if (quotes >= 3)
                {
                    _i += quotes;
                    Raw(quotes, dollars);
                    return;
                }
            }
            _i++;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '"')
                {
                    if (verbatim && At(1) == '"')
                    {
                        _i += 2;
                        continue;
                    }
                    _i++;
                    return;
                }
                if (!verbatim && c == '\\') Escape();
                else if (!verbatim && c == '\n') return;
                else if (dollars > 0 && c is '{' or '}' && At(1) == c) _i += 2;
                else if (dollars > 0 && c == '{')
                {
                    _i++;
                    Code(inHole: true);
                    _i++;
                }
                else
                {
                    if (c is Em or En) Hit(_i);
                    _i++;
                }
            }
        }

        /// <summary>A raw string's content, from after its opening quotes to a run of as many quotes; with dollars, as
        /// many braces in a row open a hole.</summary>
        private void Raw(int quotes, int dollars)
        {
            while (_i < s.Length)
            {
                var c = s[_i];
                var run = 0;
                while (At(run) == c && c is '"' or '{') run++;
                if (c == '"')
                {
                    _i += run;
                    if (run >= quotes) return;
                }
                else if (c == '{' && dollars > 0 && run >= dollars)
                {
                    _i += run;
                    Code(inHole: true);
                    _i += dollars;
                }
                else if (c == '{')
                {
                    _i += run;
                }
                else
                {
                    if (c is Em or En) Hit(_i);
                    _i++;
                }
            }
        }

        /// <summary>A backslash escape: \u2014, \U00002014 and \x2014 (and the en dash's) are hits; each escape is skipped.</summary>
        private void Escape()
        {
            var start = _i;
            var kind = At(1);
            var digits = kind switch { 'u' => 4, 'U' => 8, 'x' => HexRun(), _ => 0 };
            if (digits > 0 && int.TryParse(s.AsSpan(_i + 2, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) && code is 0x2014 or 0x2013)
                Hit(start);
            _i += 2 + digits;
        }

        private int HexRun()
        {
            var run = 0;
            while (run < 4 && Uri.IsHexDigit(At(2 + run))) run++;
            return run;
        }
    }
}
