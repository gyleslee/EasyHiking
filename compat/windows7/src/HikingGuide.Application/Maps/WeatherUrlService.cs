using System.Globalization;

namespace HikingGuide.Application.Maps;

public static class WeatherUrlService
{
    private static string Number(double value) => value.ToString("0.0#####", CultureInfo.InvariantCulture);

    public static string Windy(double latitude, double longitude) =>
        $"https://www.windy.com/{Number(latitude)}/{Number(longitude)}";

    public static string Meteoblue(double latitude, double longitude) =>
        $"https://www.meteoblue.com/en/weather/week/{Number(Math.Abs(latitude))}{(latitude < 0 ? 'S' : 'N')}{Number(Math.Abs(longitude))}{(longitude < 0 ? 'W' : 'E')}";
}
