using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class MonitorCatalogueTests
{
    private const string Header = "brand,model_number,model_name,alternatives,inches,width,height,panel,on_w,sleep_w,off_w,max_nits,hdr,certified";

    // Real listings from the shipped table, written as the table writes them.
    private const string DellU2723Qe = "DELL,U2723QEt,U2723QE,U2723QX,27,3840,2160,IPS LCD,28.32,0.74,0.3,400,,2021-07-14";
    private const string BenqGw2480 = "BenQ,GW2480-B,GW2480,GW2480E|BL2480,23.8,1920,1080,IPS LCD,10.2,0.2,0.1,250,,2019-11-19";
    private const string Jetwing = "\"JETWING, SELENO, TICNOVA\",JWG-238,\"23.8\"\" LED Monitor\",JWG-2105|JWG-2306|JWG-2308|JWG-2308YM|JWG-2400|JWG-2700|JWG-2700YM|JWG-2800|JWG-3200|JWG-3400|TIC2308|TIC-238|TIC238WD,23.8,1920,1080,IPS LCD,11.25,0.34,,153.2,,2025-03-04";
    private const string Lg24Bn550Y = "LG,24BN550Y,\"24BN550Y-*24BN550Y-* (\"\"*\"\" can be 'blank' or A~Z, Ex.:24BN550Y-B)\",24BN550Y-*24BN550Y-*,23.8,1920,1080,IPS LCD,11.28,0.13,0.13,220,,2020-04-14";

    private static MonitorCatalogue Shipped => MonitorCatalogue.Shipped;

    private static MonitorCatalogue Parse(params string[] lines) => MonitorCatalogue.Parse(new StringReader(string.Join("\r\n", lines)));

    [Fact]
    public void The_shipped_table_is_read_once_and_holds_the_certified_monitors()
    {
        Shipped.ShouldBeSameAs(MonitorCatalogue.Shipped);
        Shipped.Monitors.Count.ShouldBeGreaterThan(1000);
        Shipped.Monitors.ShouldContain(new CatalogueMonitor("DELL", "U2723QEt", "U2723QE", 27, 3840, 2160, "IPS LCD", 28.32, 0.74, 400));
    }

    [Fact]
    public void Dell_is_found_under_the_model_number_it_lists_with_a_revision_letter()
    {
        var monitor = Shipped.Find("DEL", "DELL U2723QE", 27, 3840, 2160).ShouldNotBeNull();
        monitor.ModelNumber.ShouldBe("U2723QEt");
        monitor.OnW.ShouldBe(28.32);
        monitor.SleepW.ShouldBe(0.74);

        // E2318HNf gives E2318HX as its model name, so only the number without its revision letter names the E2318HN.
        Shipped.Find("DEL", "DELL E2318HN", 23, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("E2318HNf");
    }

    [Fact]
    public void A_single_trailing_x_or_y_is_part_of_the_name_not_a_placeholder()
    {
        // The U2723QE is also sold as the U2723QX: the X is a real letter, so it doesn't stand for any other one.
        Shipped.Find("DEL", "DELL U2723QX", 27, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("U2723QEt");
        Shipped.Find("DEL", "DELL U2723QA", 27, 3840, 2160).ShouldBeNull();

        // Acer lists B247Y*** for its B247Y family; the Y is not a placeholder, so a B247 is none of them.
        Shipped.Find("ACR", "Acer B247", 23.8, 1920, 1080).ShouldBeNull();
    }

    [Fact]
    public void Acer_is_found_without_the_suffix_it_lists_models_with()
    {
        Shipped.Find("ACR", "Acer B196L", 19, 1280, 1024).ShouldNotBeNull().ModelName.ShouldBe("B196L_q");

        // EK271U_a is listed under no other name; SA272_a's EK27***** would take it if the suffix didn't name it whole.
        var monitor = Shipped.Find("ACR", "Acer EK271U", 27, 1920, 1080).ShouldNotBeNull();
        monitor.ModelNumber.ShouldBe("EK271U_a");
        monitor.OnW.ShouldBe(13.18);
    }

    [Fact]
    public void Hp_is_found_whether_or_not_its_name_says_monitor()
    {
        Shipped.Find("HPN", "HP 322pb", 21.5, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("322pb");
        Shipped.Find("HWP", "HP 322pb Monitor", 21.5, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("322pb");
    }

    [Fact]
    public void Lenovo_and_philips_put_their_maker_code_in_front_of_the_name()
    {
        Shipped.Find("LEN", "LEN S24e-10", 23.8, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("A18238FS0");
        Shipped.Find("PHL", "PHL 221V8", 21.5, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("221V8");
    }

    [Fact]
    public void Viewsonic_is_found_by_its_marketing_name_or_the_internal_code_it_lists_as_the_model_number()
    {
        Shipped.Find("VSC", "VA2247-MH", 21.5, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("VS18521");
        Shipped.Find("VSC", "VS18521", 21.5, 1920, 1080).ShouldNotBeNull().ModelName.ShouldBe("VA2247-mh");
    }

    [Fact]
    public void A_monitor_is_found_without_the_series_word_the_list_puts_before_its_model()
    {
        // EIZO lists the EV2740X as the FlexScan EV2740X, and MSI the MP243X as the PRO MP243X; the monitors leave the word out.
        var eizo = Shipped.Find("ENC", "EV2740X", 27, 3840, 2160).ShouldNotBeNull();
        eizo.ModelName.ShouldBe("FlexScan EV2740X");
        eizo.OnW.ShouldBe(17);
        Shipped.Find("MSI", "MSI MP243X", 24, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("PRO MP243X");

        // Only the last word names the model: MSI's PRO MP241 E14V is no MP241.
        Shipped.Find("MSI", "MSI MP241", 23.8, 1920, 1080).ShouldBeNull();
    }

    [Fact]
    public void A_name_edid_cut_short_at_thirteen_characters_matches_the_longer_names_it_begins()
    {
        // EDID holds thirteen characters of a name, so the VG27AQML1A and the VX2776-4K-MHDU can name themselves only this far.
        Shipped.Find("AUS", "ASUS VG27AQML", 27, 2560, 1440).ShouldNotBeNull().ModelNumber.ShouldBe("VG27AQML1A");
        Shipped.Find("VSC", "VX2776-4K-mhd", 27, 3840, 2160).ShouldNotBeNull().ModelName.ShouldBe("VX2776-4K-MHDU");

        // The size must still agree, and a shorter name was not cut.
        Shipped.Find("AUS", "ASUS VG27AQML", 32, 2560, 1440).ShouldBeNull();
        Shipped.Find("AUS", "ASUS VG27AQM", 27, 2560, 1440).ShouldBeNull();
    }

    [Fact]
    public void A_whole_name_of_thirteen_characters_still_wins_over_the_longer_names_it_begins()
    {
        // Philips lists the 27B2N2100 at 13.48 W beside the 27B2N2100A and 27B2N2100F, whose median would be 13.86 W.
        var monitor = Shipped.Find("PHL", "PHL 27B2N2100", 27, 1920, 1080).ShouldNotBeNull();
        monitor.ModelNumber.ShouldBe("27B2N2100");
        monitor.OnW.ShouldBe(13.48);
    }

    [Fact]
    public void A_monitor_listed_more_than_once_takes_the_median_of_its_listings()
    {
        // BenQ lists the GW2480 twice, at 10.2 W and 10 W.
        var benq = Shipped.Find("BNQ", "BenQ GW2480", 23.8, 1920, 1080).ShouldNotBeNull();
        benq.ModelNumber.ShouldBe("GW2480-B");
        benq.OnW.ShouldBe(10.1, 1e-9);
        benq.SleepW.ShouldBe(0.2, 1e-9);

        // Nine listings name the B247Y, from 9.48 W to 14.93 W; the first in the table gives the name.
        var acer = Shipped.Find("ACR", "Acer B247Y", 23.8, 1920, 1080).ShouldNotBeNull();
        acer.ModelName.ShouldBe("B247Y");
        acer.OnW.ShouldBe(13.35, 1e-9);
        acer.SleepW.ShouldBe(0.2, 1e-9);
    }

    [Fact]
    public void A_placeholder_stands_for_up_to_as_many_characters_as_it_has()
    {
        // LG lists 27UP850-*, where * is a colour letter or nothing, and 27UP850N-* beside it.
        Shipped.Find("GSM", "27UP850-W", 27, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("27UP850");
        Shipped.Find("GSM", "27UP850", 27, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("27UP850");
        Shipped.Find("GSM", "27UP850N-W", 27, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("27UP850N");
        Shipped.Find("GSM", "27UP85", 27, 3840, 2160).ShouldBeNull();

        // Acer's 22UT****** stands for up to six.
        Shipped.Find("ACR", "22UTABCDEF", 21.5, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("22UT2Q");
        Shipped.Find("ACR", "22UTABCDEFG", 21.5, 1920, 1080).ShouldBeNull();
    }

    [Fact]
    public void Placeholder_runs_count_together_across_a_dash_and_include_runs_of_x()
    {
        // LG's 40B990##-# is three placeholders.
        Shipped.Find("GSM", "40B990AB-C", 39.7, 5120, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("40WT95UF-#");
        Shipped.Find("GSM", "40B990ABCD", 39.7, 5120, 2160).ShouldBeNull();

        // Acer's PE140WUXXXX is four.
        Shipped.Find("ACR", "Acer PE140WUABCD", 14, 1920, 1200).ShouldNotBeNull().ModelNumber.ShouldBe("PE140WU_z");
        Shipped.Find("ACR", "Acer PE140WUABCDE", 14, 1920, 1200).ShouldBeNull();
    }

    [Fact]
    public void Planar_writes_its_placeholders_as_y()
    {
        // Planar lists the 2E0I1 as 2E0I1yyyyy too. Were the y's letters, that would be a name of the model's own, which a
        // maker without a brand here may match; as placeholders it is only the model's family, which such a maker may not.
        Shipped.Find("PNR", "2E0I1", 85.5, 3840, 2160).ShouldNotBeNull().Brand.ShouldBe("PLANAR");
        Shipped.Find("PNR", "2E0I1YYYYY", 85.5, 3840, 2160).ShouldBeNull();
    }

    [Fact]
    public void A_placeholder_inside_a_name_can_not_be_matched_as_written_so_it_matches_nothing()
        // Samsung lists its S24R650FDN as S24R65*FD#; dropping the * would wrongly take an S24R65FD.
        => Shipped.Find("SAM", "S24R65FD", 23.8, 1920, 1080).ShouldBeNull();

    [Fact]
    public void A_matching_resolution_wins_whichever_way_round_it_is_listed()
    {
        // Acer lists the V206HQL at 1366 × 768, at 1600 × 900, and, under the B206HQL, at 900 × 1600.
        var wide = Shipped.Find("ACR", "Acer V206HQL", 19.5, 1600, 900).ShouldNotBeNull();
        wide.ModelNumber.ShouldBe("B206HQL");
        wide.OnW.ShouldBe(9.98, 1e-9);

        Shipped.Find("ACR", "Acer V206HQL", 19.5, 1366, 768).ShouldNotBeNull().OnW.ShouldBe(7.5);
    }

    [Theory]
    // ViewSonic lists the family VX24*********** under its 1080p VX2418, which would take in the 4K VX2478-4K-HD.
    [InlineData("VSC", "VX2478-4K-HD", 23.8, 3840, 2160)]
    // Acer lists SA27***** under the 1080p KA272 and SA272 and the 4K EK271K, but under no 2560 × 1440 monitor.
    [InlineData("ACR", "SA272U", 27, 2560, 1440)]
    // AOpen lists 27HC***** under its 1080p 27CL1, which would take in the 2560 × 1440 27HC5UR.
    [InlineData("AOP", "27HC5UR", 27, 2560, 1440)]
    public void A_family_takes_in_a_monitor_only_where_it_is_listed_at_the_monitors_resolution(
        string maker, string name, double inches, int width, int height)
        => Shipped.Find(maker, name, inches, width, height).ShouldBeNull();

    [Fact]
    public void A_family_still_takes_in_a_monitor_at_a_resolution_it_is_listed_at_or_one_that_gives_none()
    {
        // The 27HC5R is the 27HC5UR's 1080p sibling.
        Shipped.Find("AOP", "27HC5R", 27, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("27CL1_a");
        Shipped.Find("AOP", "27HC5R", 27, 0, 0).ShouldNotBeNull().ModelNumber.ShouldBe("27CL1_a");

        // LG lists its 3840 × 2160 27BQ65UB-* at 2160 × 3840, the same resolution the other way round.
        Shipped.Find("GSM", "27BQ65UB-B", 27, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("27BQ65UB");
        Shipped.Find("GSM", "27BQ65UB-B", 27, 2560, 1440).ShouldBeNull();
    }

    [Fact]
    public void A_name_cut_short_also_needs_a_listing_at_the_monitors_resolution()
        // ASUS lists the VG27AQML1A at 2560 × 1440 only.
        => Shipped.Find("AUS", "ASUS VG27AQML", 27, 3840, 2160).ShouldBeNull();

    [Fact]
    public void An_exact_name_keeps_its_listing_whatever_resolution_the_monitor_gives()
    {
        // Real EDIDs of Dell's 5120 × 2160 U4025QW give 2560 × 1080, and the list has Philips' 329P1 at 3840 × 2169. Each is
        // the model all the same, and an estimate from the resolution the monitor gives would be further off.
        var dell = Shipped.Find("DEL", "U4025QW", 40, 2560, 1080).ShouldNotBeNull();
        dell.ModelNumber.ShouldBe("U4025QWt");
        dell.OnW.ShouldBe(44.72);
        Shipped.Find("PHL", "329P1", 31.5, 3840, 2160).ShouldNotBeNull().ModelName.ShouldBe("329P1RN");
    }

    [Fact]
    public void A_series_word_left_out_keeps_its_listing_whatever_resolution_the_monitor_gives()
    {
        // EIZO lists the EV2456 as the 1920 × 1200 FlexScan EV2456, and real EDIDs of it give 1920 × 1080. It is the
        // model all the same, and the estimate for 1920 × 1080 would be further off.
        var eizo = Shipped.Find("ENC", "EV2456", 24, 1920, 1080).ShouldNotBeNull();
        eizo.ModelName.ShouldBe("FlexScan EV2456");
        eizo.OnW.ShouldBe(14.72);
    }

    [Fact]
    public void The_closest_size_wins_among_listings_that_agree_on_resolution()
    {
        // Acer lists the B227Q at 22 inches once and, under three listings, at 21.5.
        Shipped.Find("ACR", "Acer B227Q", 22, 1920, 1080).ShouldNotBeNull().ModelName.ShouldBe("B227Q_b");
        Shipped.Find("ACR", "Acer B227Q", 21.5, 1920, 1080).ShouldNotBeNull().OnW.ShouldBe(11.91, 1e-9);
    }

    [Fact]
    public void A_vague_name_matches_nothing()
    {
        Shipped.Find("GSM", "LG HDR 4K", 0, 3840, 2160).ShouldBeNull();
        Shipped.Find("GSM", "LG HDR 4K", 27, 3840, 2160).ShouldBeNull();
    }

    [Fact]
    public void A_real_model_at_the_wrong_size_matches_nothing()
        => Shipped.Find("DEL", "DELL U2723QE", 24, 3840, 2160).ShouldBeNull();

    [Fact]
    public void Without_a_size_a_name_matches_only_when_its_listings_agree_on_one()
    {
        Shipped.Find("DEL", "DELL U2723QE", 0, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("U2723QEt");

        // ASUS lists the MQ149CD as a 28-inch pair of screens and, under the MQ14FCDV, as a 14-inch one.
        Shipped.Find("AUS", "ASUS MQ149CD", 0, 1920, 1200).ShouldBeNull();
        Shipped.Find("AUS", "ASUS MQ149CD", 14, 1920, 1200).ShouldNotBeNull().ModelNumber.ShouldBe("MQ14FCDV");
    }

    [Fact]
    public void A_known_maker_matches_only_its_own_brand_and_an_unknown_one_any()
    {
        Shipped.Find("GSM", "U2723QE", 27, 3840, 2160).ShouldBeNull();
        Shipped.Find("XYZ", "U2723QE", 27, 3840, 2160).ShouldNotBeNull().Brand.ShouldBe("DELL");
        Shipped.Find("", "U2723QE", 27, 3840, 2160).ShouldNotBeNull().Brand.ShouldBe("DELL");
    }

    [Fact]
    public void An_unknown_maker_matches_only_a_name_the_list_gives_whole()
    {
        // A family could be any maker's: Vizio's VA220E is no ViewSonic of the VA22*********** family, Armaggeddon's XF24HD
        // no Acer of XF24*****, and VIZTA's 27N50, a 4K monitor, no 1080p Koorui of 27N5******.
        Shipped.Find("VIZ", "VA220E", 21.5, 1920, 1080).ShouldBeNull();
        Shipped.Find("ARM", "XF24HD", 23.8, 1920, 1080).ShouldBeNull();
        Shipped.Find("JRY", "27N50", 27, 3840, 2160).ShouldBeNull();

        // So could a series word or a name cut short: EIZO's FlexScan EV2740X and ViewSonic's VX2776-4K-MHDU are found only
        // by their own makers.
        Shipped.Find("XYZ", "EV2740X", 27, 3840, 2160).ShouldBeNull();
        Shipped.Find("ENC", "EV2740X", 27, 3840, 2160).ShouldNotBeNull().ModelNumber.ShouldBe("FlexScan EV2740X");
        Shipped.Find("XYZ", "VX2776-4K-mhd", 27, 3840, 2160).ShouldBeNull();
        Shipped.Find("VSC", "VX2776-4K-mhd", 27, 3840, 2160).ShouldNotBeNull().ModelName.ShouldBe("VX2776-4K-MHDU");
    }

    [Fact]
    public void The_codes_asus_and_aopen_monitors_report_match_only_their_own_brand()
    {
        // ASUS's older monitors report ACI. Taken for any maker's, it left "ASUS" in front of the VP228's name, and let
        // ViewSonic's VX24*********** and VG24********** families take in the VX24A and the VG248.
        Shipped.Find("ACI", "ASUS VP228", 21.5, 1920, 1080).ShouldNotBeNull().ModelName.ShouldBe("VP228QG");
        Shipped.Find("ACI", "VX24A", 24, 2560, 1440).ShouldBeNull();
        Shipped.Find("ACI", "VG248", 24, 1920, 1080).ShouldBeNull();

        // AOpen's report AOP, which Acer's 16PMXXXXXX, a 2880 × 1800 portable, took in; AOpen's own families still take them.
        Shipped.Find("AOP", "16PM6Q", 15.6, 1920, 1080).ShouldBeNull();
        Shipped.Find("AOP", "27HC5R", 27, 1920, 1080).ShouldNotBeNull().ModelNumber.ShouldBe("27CL1_a");
    }

    [Theory]
    [InlineData("DELL U2723QE", "DELL", "U2723QE")]
    [InlineData("HP 322pb Monitor", "HP", "322PB")]
    [InlineData("LEN T24i-10", "Lenovo", "T24I10")]
    [InlineData("Lenovo T24i-10", "Lenovo", "T24I10")]
    [InlineData("PHL 243V7", "PHILIPS", "243V7")]
    [InlineData("BenQ GW2480-B", "BenQ", "GW2480B")]
    [InlineData(" u2723qe ", null, "U2723QE")]
    [InlineData("LED Display", null, "LED")]
    [InlineData("U2723QE DisplayPort", null, "U2723QEDISPLAYPORT")]
    [InlineData("U2723QE DELL", "DELL", "U2723QEDELL")]
    [InlineData("DELL", "DELL", "DELL")]
    [InlineData("T24i-** ( “*” can be A-Z)", "Lenovo", "T24ICANBEAZ")]
    public void A_name_is_compared_as_letters_and_digits_without_the_maker_or_the_words_monitor_and_display(
        string text, string? brand, string expected)
        => MonitorCatalogue.Normalise(text, brand).ShouldBe(expected);

    [Theory]
    [InlineData("DEL", "DELL")]
    [InlineData("GSM", "LG")]
    [InlineData("SAM", "Samsung")]
    [InlineData("ACR", "Acer")]
    [InlineData("AUS", "ASUS")]
    [InlineData("BNQ", "BenQ")]
    [InlineData("HPN", "HP")]
    [InlineData("LEN", "Lenovo")]
    [InlineData("PHL", "PHILIPS")]
    [InlineData("VSC", "ViewSonic")]
    [InlineData("ENC", "EIZO")]
    [InlineData("AOA", "AOpen")]
    [InlineData("ELO", "ELO")]
    [InlineData("SHP", "Sharp")]
    [InlineData("ACI", "ASUS")]
    [InlineData("AOP", "AOpen")]
    [InlineData("APP", "Apple")]
    [InlineData("HEC", "Hisense")]
    [InlineData("TSB", "Toshiba")]
    [InlineData(" del ", "DELL")]
    public void A_maker_code_gives_the_brand_the_table_lists_it_under(string code, string brand)
        => MonitorMakers.Brand(code).ShouldBe(brand);

    [Theory]
    [InlineData("XYZ")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_missing_maker_code_has_no_brand(string? code)
        => MonitorMakers.Brand(code).ShouldBeNull();

    [Fact]
    public void Every_brand_with_a_maker_code_is_spelt_as_the_table_spells_it_and_every_big_one_has_a_code()
    {
        var brands = Shipped.Monitors.GroupBy(monitor => monitor.Brand).ToList();
        foreach (var brand in brands)
        {
            foreach (var code in MonitorMakers.Codes(brand.Key)) MonitorMakers.Brand(code).ShouldBe(brand.Key);
        }
        foreach (var brand in brands.Where(group => group.Count() > 10))
        {
            MonitorMakers.Codes(brand.Key).ShouldNotBeEmpty($"{brand.Key} has {brand.Count()} monitors in the table");
        }
    }

    [Fact]
    public void Either_line_ending_reads_the_same()
    {
        string[] lines = [Header, DellU2723Qe, BenqGw2480];
        var crlf = MonitorCatalogue.Parse(new StringReader(string.Join("\r\n", lines) + "\r\n"));
        var lf = MonitorCatalogue.Parse(new StringReader(string.Join("\n", lines) + "\n"));
        var unterminated = MonitorCatalogue.Parse(new StringReader(string.Join("\n", lines)));

        crlf.Monitors.Count.ShouldBe(2);
        lf.Monitors.ShouldBe(crlf.Monitors);
        unterminated.Monitors.ShouldBe(crlf.Monitors);
        lf.Find("DEL", "DELL U2723QX", 27, 3840, 2160).ShouldNotBeNull();
    }

    [Fact]
    public void Quoted_fields_keep_their_commas_and_doubled_quotes()
    {
        var catalogue = Parse(Header, Jetwing, Lg24Bn550Y);

        var jetwing = catalogue.Monitors[0];
        jetwing.Brand.ShouldBe("JETWING, SELENO, TICNOVA");
        jetwing.ModelName.ShouldBe("23.8\" LED Monitor");
        jetwing.Inches.ShouldBe(23.8);
        catalogue.Monitors[1].ModelName.ShouldBe("24BN550Y-*24BN550Y-* (\"*\" can be 'blank' or A~Z, Ex.:24BN550Y-B)");

        catalogue.Find("", "JWG-2308", 23.8, 1920, 1080).ShouldBeSameAs(jetwing);
        catalogue.Find("GSM", "LG 24BN550Y", 23.8, 1920, 1080).ShouldBeSameAs(catalogue.Monitors[1]);
    }

    [Fact]
    public void A_luminance_of_zero_is_unknown_and_a_missing_sleep_figure_is_the_typical_one()
    {
        Shipped.Monitors.Single(monitor => monitor.Brand == "ASUS" && monitor.ModelNumber == "MS27UC").MaxNits.ShouldBeNull();

        var monitor = Parse(Header, "ASUS,MS27UC,MS27UC,MS27*****|MS27UCE,27,3840,2160,IPS LCD,24.87,,0.09,,,2024-07-17").Monitors.Single();
        monitor.SleepW.ShouldBe(0.2);
        monitor.MaxNits.ShouldBeNull();
    }

    [Fact]
    public void A_table_missing_a_column_or_holding_a_number_that_is_not_one_is_refused_saying_where()
    {
        Should.Throw<InvalidDataException>(() => Parse("brand,model_number,model_name,inches,width,height,panel,on_w,sleep_w,max_nits"))
            .Message.ShouldContain("alternatives");
        Should.Throw<InvalidDataException>(() => Parse(Header, DellU2723Qe.Replace("28.32", "lots")))
            .Message.ShouldContain("line 2");
        Should.Throw<InvalidDataException>(() => Parse(Header, DellU2723Qe + ",extra"))
            .Message.ShouldContain("line 2");
    }
}
