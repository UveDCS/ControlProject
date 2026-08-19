namespace AtcListener;

// Formulas de navegacion basicas (esfera, no elipsoide - de sobra de precision para vectores de ATC).
public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;

    // Distancia en millas nauticas y rumbo inicial (grados, desde el norte) DESDE (fromLat,fromLon) HACIA (toLat,toLon).
    public static (double DistanceNm, double BearingDeg) DistanceAndBearing(
        double fromLat, double fromLon, double toLat, double toLon)
    {
        var lat1 = DegToRad(fromLat);
        var lat2 = DegToRad(toLat);
        var deltaLat = DegToRad(toLat - fromLat);
        var deltaLon = DegToRad(toLon - fromLon);

        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        var distanceNm = EarthRadiusNm * c;

        var y = Math.Sin(deltaLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);
        var bearingDeg = (RadToDeg(Math.Atan2(y, x)) + 360) % 360;

        return (distanceNm, bearingDeg);
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;
    private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;
}
