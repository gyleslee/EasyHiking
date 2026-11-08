using System.Globalization;
using HikingGuide.Application.Maps;

namespace HikingGuide.Application.Tests;

public sealed class WeatherUrlServiceTests
{
    [Theory]
    [InlineData(24.479214, 113.263840, "24.479214N113.26384E")]
    [InlineData(24.479214, -113.263840, "24.479214N113.26384W")]
    [InlineData(-24.479214, 113.263840, "24.479214S113.26384E")]
    [InlineData(-24.479214, -113.263840, "24.479214S113.26384W")]
    public void Meteoblue_UsesCorrectHemispheres(double latitude, double longitude, string token)
    {
        Assert.Equal("https://www.meteoblue.com/en/weather/week/" + token,
            WeatherUrlService.Meteoblue(latitude, longitude));
    }

    [Fact]
    public void WeatherLinks_UseInvariantDecimalAndActualContextCoordinate()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal("https://www.windy.com/24.479214/113.26384",
                WeatherUrlService.Windy(24.479214, 113.263840));
            Assert.Equal("https://www.windy.com/24.0/113.0", WeatherUrlService.Windy(24, 113));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
