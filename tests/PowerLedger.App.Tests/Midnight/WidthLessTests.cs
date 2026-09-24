using System.Globalization;
using System.Windows;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>The room left in a card once the columns beside a piece of text are taken, for text that must wrap inside a control that lays it out on one line.</summary>
public class WidthLessTests
{
    [Fact]
    public void A_width_less_the_parameter_never_goes_below_nothing()
    {
        var less = new WidthLess();
        less.Convert(600.0, typeof(double), "296", CultureInfo.InvariantCulture).ShouldBe(304.0);
        less.Convert(200.0, typeof(double), "296", CultureInfo.InvariantCulture).ShouldBe(0.0);
    }

    [Fact]
    public void Before_the_card_is_laid_out_the_text_is_not_held_in()
    {
        var less = new WidthLess();
        less.Convert(0.0, typeof(double), "296", CultureInfo.InvariantCulture).ShouldBe(double.PositiveInfinity);
        less.Convert(DependencyProperty.UnsetValue, typeof(double), "296", CultureInfo.InvariantCulture).ShouldBe(double.PositiveInfinity);
    }
}
