using System.Globalization;

namespace Contoso.Lending.Domain;

/// <summary>
/// The legacy desk ran under en-US and the result strings were formatted with the ambient
/// UI culture (`ToString("C2")` renders as "$1,956.61"). Every golden record carries
/// `"culture": "en-US"`, so the service pins that culture in code instead of inheriting the
/// host locale.
/// </summary>
public static class LegacyCulture
{
    public static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
}
