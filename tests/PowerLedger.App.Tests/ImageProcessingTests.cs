using Shouldly;

namespace PowerLedger.App.Tests;

public class ImageProcessingTests
{
    [Fact]
    public void A_small_plain_image_stays_a_png()
    {
        var source = TestImages.Png(400, 300);

        var (data, contentType) = ImageProcessing.Process(source)!.Value;

        contentType.ShouldBe("image/png");
        data.Length.ShouldBeLessThanOrEqualTo(ImageProcessing.MaxBytes);
    }

    [Fact]
    public void A_wide_image_is_downscaled_to_1600_on_the_long_side()
    {
        var source = TestImages.Png(3200, 1600);

        var (data, _) = ImageProcessing.Process(source)!.Value;

        Sta.Run(() =>
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                new System.IO.MemoryStream(data), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Math.Max(frame.PixelWidth, frame.PixelHeight).ShouldBe(ImageProcessing.MaxDimension);
            return true;
        });
    }

    /// <summary>A busy image that won't compress well as a PNG falls back to JPEG, brought under the 1 MB limit.</summary>
    [Fact]
    public void A_busy_image_that_wont_shrink_as_a_png_falls_back_to_jpeg_under_1mb()
    {
        var source = TestImages.Png(1400, 1400, noisy: true);

        var (data, contentType) = ImageProcessing.Process(source)!.Value;

        contentType.ShouldBe("image/jpeg");
        data.Length.ShouldBeLessThanOrEqualTo(ImageProcessing.MaxBytes);
    }

    [Fact]
    public void Bytes_that_arent_an_image_are_refused()
    {
        ImageProcessing.Process([1, 2, 3, 4, 5]).ShouldBeNull();
    }
}
