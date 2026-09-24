using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PowerLedger.App;

/// <summary>
/// The report as an A4 PDF (spec §9 exports and the monthly report), drawn with QuestPDF from a <see cref="ReportData"/>
/// in the light palette so it prints well. Bars are rows with coloured backgrounds, not pictures.
/// </summary>
internal static class ReportDocument
{
    private const string Ink = "#1D1F1B";
    private const string Ink2 = "#575B53";
    private const string Ink3 = "#858980";
    private const string Rule = "#CDD0C8";
    private const string Amber = "#A2680C";
    private static readonly string[] PartColours = ["#C4761C", "#4A76A6", "#7C8A2E", "#8E928A"];
    private static readonly string[] QualityColours = ["#3E7E43", "#35678A", "#7C7355"];

    /// <summary>A4's width, 595 pt, less the two 40 pt margins.</summary>
    private const float ContentWidth = 515;
    /// <summary>Segoe UI, then the fonts Windows ships for the scripts it lacks: Indic, Thai and Lao, Chinese, Japanese, Korean, Ethiopic, symbols.</summary>
    private static readonly string[] Fonts =
        ["Segoe UI", "Nirmala UI", "Leelawadee UI", "Microsoft YaHei UI", "Microsoft JhengHei UI", "Yu Gothic UI", "Malgun Gothic", "Ebrima", "Segoe UI Symbol"];

    static ReportDocument()
    {
        QuestPDF.Settings.License = LicenseType.Community;          // spec §14: QuestPDF's Community licence
        QuestPDF.Settings.UseSystemFonts = true;                    // off by default since 2026.9.0; Windows' own fonts cover the scripts
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;         // a glyph no font has is an empty box, not a failed export
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;       // a script font missing from this Windows is skipped
    }

    /// <summary>The PDF's bytes: the main report, then a page per household member when "Include my household" is ticked
    /// (Plan N task A5).</summary>
    public static byte[] Generate(ReportData data, string version, DateTimeOffset made, CultureInfo culture)
        => Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style.FontFamily(Fonts).FontSize(9.5f).FontColor(Ink));
                page.Header().Column(header =>
                {
                    header.Item().Text("POWERLEDGER · ENERGY REPORT").FontSize(8).SemiBold().FontColor(Amber).LetterSpacing(0.08f);
                    header.Item().PaddingTop(4).Text(data.Title).FontSize(22).SemiBold();
                    header.Item().Text(data.Period).FontColor(Ink2);
                });
                page.Content().PaddingTop(18).Column(column => Body(column, data, culture));
                Footer(page, version, made, culture);
            });
            if (data.Household is { } household)
            {
                foreach (var member in household.Members) document.Page(page => MemberPage(page, data, member, version, made, culture));
            }
        }).GeneratePdf();

    /// <summary>One household member's own simple page: its energy, cost and the four bands, from household_rows.</summary>
    private static void MemberPage(PageDescriptor page, ReportData data, HouseholdMemberReportData member, string version, DateTimeOffset made, CultureInfo culture)
    {
        page.Size(PageSizes.A4);
        page.Margin(40);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(style => style.FontFamily(Fonts).FontSize(9.5f).FontColor(Ink));
        page.Header().Column(header =>
        {
            header.Item().Text("POWERLEDGER · HOUSEHOLD MEMBER").FontSize(8).SemiBold().FontColor(Amber).LetterSpacing(0.08f);
            header.Item().PaddingTop(4).Text(member.Name).FontSize(22).SemiBold();
            header.Item().Text($"{member.Kind} · {data.Period}").FontColor(Ink2);
        });
        page.Content().PaddingTop(18).Column(column =>
        {
            column.Spacing(18);
            column.Item().Column(bill =>
            {
                Heading(bill, "Bill");
                bill.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(hero =>
                {
                    hero.RelativeItem().AlignBottom().Text("Energy").FontColor(Ink2);
                    hero.AutoItem().Text(text =>
                    {
                        text.Span(member.Energy).FontSize(24).Light();
                        text.Span(UnitAfter(member.Energy, "kWh")).FontColor(Ink3);
                    });
                });
                if (member.Costs.Count == 0) Line(bill, "Cost", Format.Missing, "no tariff set");
                foreach (var cost in member.Costs) Line(bill, "Cost", cost.Cost, cost.Currency);
            });
            column.Item().Column(parts =>
            {
                Heading(parts, "By component");
                if (member.Parts.Any(p => p.Fraction > 0))
                {
                    parts.Item().PaddingVertical(6).Height(8).Row(bar =>
                    {
                        for (var i = 0; i < member.Parts.Count; i++)
                        {
                            if (member.Parts[i].Fraction > 0) bar.RelativeItem((float)member.Parts[i].Fraction).Background(PartColours[i]);
                        }
                    });
                }
                for (var i = 0; i < member.Parts.Count; i++)
                {
                    var part = member.Parts[i];
                    parts.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(line =>
                    {
                        line.ConstantItem(14).AlignMiddle().AlignLeft().Width(7).Height(7).Background(PartColours[i]);
                        line.RelativeItem().Text(part.Name).FontColor(Ink2);
                        line.AutoItem().Text(WithUnit(part.Energy, "kWh")).SemiBold();
                        line.ConstantItem(40).AlignRight().Text(part.Share).FontColor(Ink3);
                    });
                }
            });
        });
        Footer(page, version, made, culture);
    }

    private static void Footer(PageDescriptor page, string version, DateTimeOffset made, CultureInfo culture)
        => page.Footer().Row(row =>
        {
            row.RelativeItem().Text($"PowerLedger {version} · made {made.ToString("d MMM yyyy HH:mm", culture)}").FontSize(7.5f).FontColor(Ink3);
            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Ink3));
                text.Span("page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });

    private static void Body(ColumnDescriptor column, ReportData data, CultureInfo culture)
    {
        column.Spacing(18);
        if (!data.HasData) column.Item().Text("No readings were recorded in this range.").FontColor(Ink2);

        column.Item().Row(row =>
        {
            row.Spacing(28);
            row.RelativeItem().Column(bill =>
            {
                Heading(bill, "Bill");
                bill.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(hero =>
                {
                    hero.RelativeItem().AlignBottom().Text("Energy").FontColor(Ink2);
                    hero.AutoItem().Text(text =>
                    {
                        text.Span(data.Energy).FontSize(24).Light();
                        text.Span(UnitAfter(data.Energy, "kWh")).FontColor(Ink3);
                    });
                });
                Line(bill, "Cost", data.Cost, data.CostNote);
                Line(bill, "CO₂", data.Co2, data.Co2Note);
                Line(bill, "Average", WithUnit(data.Average, "W"), data.Average == Format.Missing ? "" : "while on");
                Line(bill, "Peak", WithUnit(data.Peak, "W"), data.PeakAt);
            });
            row.RelativeItem().Column(time =>
            {
                Heading(time, "Time");
                Line(time, "On", data.On, "");
                Line(time, "Idle, display on", data.IdleOn, "");
                Line(time, "Idle, display off", data.IdleOff, "");
                Line(time, "Asleep", data.Asleep, "while the service ran");
                Line(time, "Unmonitored", data.Unmonitored, "no readings at all");
            });
        });

        column.Item().Row(row =>
        {
            row.Spacing(28);
            row.RelativeItem().Column(parts =>
            {
                Heading(parts, "By component");
                if (data.Parts.Any(p => p.Fraction > 0))
                {
                    parts.Item().PaddingVertical(6).Height(8).Row(bar =>
                    {
                        for (var i = 0; i < data.Parts.Count; i++)
                        {
                            if (data.Parts[i].Fraction > 0) bar.RelativeItem((float)data.Parts[i].Fraction).Background(PartColours[i]);
                        }
                    });
                }
                for (var i = 0; i < data.Parts.Count; i++)
                {
                    var part = data.Parts[i];
                    var colour = PartColours[i];
                    parts.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(line =>
                    {
                        line.ConstantItem(14).AlignMiddle().AlignLeft().Width(7).Height(7).Background(colour);
                        line.RelativeItem().Text(part.Name).FontColor(Ink2);
                        line.AutoItem().Text(WithUnit(part.Energy, "kWh")).SemiBold();
                        line.ConstantItem(40).AlignRight().Text(part.Share).FontColor(Ink3);
                    });
                }
            });
            row.RelativeItem().Column(equivalents =>
            {
                Heading(equivalents, "Everyday equivalents");
                foreach (var line in data.Equivalents) Line(equivalents, line.Name, line.Value, line.Note);
            });
        });

        column.Item().Column(daily =>
        {
            Heading(daily, "Daily energy · kWh");
            var max = data.Days.Count > 0 ? data.Days.Max(d => d.Kwh) : 0;
            var gap = data.Days.Count > 120 ? 0 : data.Days.Count > 40 ? 0.5f : 2;
            daily.Item().PaddingTop(8).Height(90).Row(bars =>
            {
                foreach (var day in data.Days)
                {
                    var height = max > 0 ? (float)(86 * day.Kwh / max) : 0;
                    var slot = bars.RelativeItem().AlignBottom().PaddingHorizontal(gap);
                    if (height >= 0.5f) slot.Height(height).Background(Amber);
                }
            });
            // A label spans the days up to the next one, so it has room however narrow a day is; it starts near its day's middle.
            var every = Math.Max(1, (int)Math.Ceiling(data.Days.Count / 16.0));
            var inset = Math.Max(0, ContentWidth / data.Days.Count / 2 - 3);
            daily.Item().PaddingTop(2).Row(labels =>
            {
                for (var i = 0; i < data.Days.Count; i += every)
                {
                    labels.RelativeItem(Math.Min(every, data.Days.Count - i)).PaddingLeft(inset)
                        .Text(data.Days[i].Day.Day.ToString(CultureInfo.InvariantCulture)).FontSize(7).FontColor(Ink3);
                }
            });
            if (max > 0) daily.Item().AlignRight().Text($"highest day {Format.Kwh(max, culture)} kWh").FontSize(7.5f).FontColor(Ink3);
        });

        column.Item().Row(row =>
        {
            row.Spacing(28);
            row.RelativeItem().Column(idle =>
            {
                Heading(idle, "Idle waste");
                Line(idle, "Energy while idle", data.IdleWaste, data.IdleWasteNote);
                idle.Item().PaddingTop(6).Text(data.Advice).FontColor(Ink2);
            });
            row.RelativeItem().Column(quality =>
            {
                Heading(quality, "Data quality");
                double[] shares = [data.Quality.Measured, data.Quality.Calibrated, data.Quality.Estimated];
                if (shares.Sum() > 0)
                {
                    quality.Item().PaddingVertical(6).Height(8).Row(bar =>
                    {
                        for (var i = 0; i < shares.Length; i++)
                        {
                            if (shares[i] > 0) bar.RelativeItem((float)shares[i]).Background(QualityColours[i]);
                        }
                    });
                }
                quality.Item().Text(data.QualityText).FontColor(Ink2);
                quality.Item().PaddingTop(4).Text(ReportData.QualityLegend).FontSize(7.5f).FontColor(Ink3);
            });
        });

        if (data.Household is { } household)
        {
            column.Item().Column(combined =>
            {
                Heading(combined, "Household");
                combined.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(hero =>
                {
                    hero.RelativeItem().AlignBottom().Text("Combined energy, every PC").FontColor(Ink2);
                    hero.AutoItem().Text(text =>
                    {
                        text.Span(household.Energy).FontSize(24).Light();
                        text.Span(UnitAfter(household.Energy, "kWh")).FontColor(Ink3);
                    });
                });
                if (household.Costs.Count == 0) Line(combined, "Combined cost", Format.Missing, "no tariff set on any PC");
                foreach (var cost in household.Costs) Line(combined, "Combined cost", cost.Cost, cost.Currency);
                combined.Item().PaddingTop(2).Text($"A page follows for each of the {household.Members.Count} member PCs.").FontSize(7.5f).FontColor(Ink3);
            });
        }
    }

    private static void Heading(ColumnDescriptor column, string text)
        => column.Item().BorderBottom(1).BorderColor(Ink3).PaddingBottom(3)
            .Text(text.ToUpperInvariant()).FontSize(7.5f).SemiBold().FontColor(Ink2).LetterSpacing(0.06f);

    private static void Line(ColumnDescriptor column, string key, string value, string note)
        => column.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(row =>
        {
            row.RelativeItem().Text(key).FontColor(Ink2);
            row.AutoItem().Text(value).SemiBold();
            if (note.Length > 0) row.AutoItem().PaddingLeft(6).AlignBottom().Text(note).FontSize(7.5f).FontColor(Ink3);
        });

    /// <summary>A figure with its unit after it, or <see cref="Format.Missing"/> alone: "N/A", never "N/A kWh".</summary>
    internal static string WithUnit(string value, string unit) => value == Format.Missing ? value : $"{value} {unit}";

    /// <summary>The unit to set after a figure in a smaller, quieter span: nothing after a missing one.</summary>
    internal static string UnitAfter(string value, string unit) => value == Format.Missing ? "" : " " + unit;
}
