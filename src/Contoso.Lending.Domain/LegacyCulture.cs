using System.Globalization;

namespace Contoso.Lending.Domain;

/// <summary>
/// BR-UI-015 Forms/*.cs (all Parse/ToString sites): the legacy client formatted
/// under the workstation's CurrentCulture; the production desktops ran en-US.
/// The service pins en-US so user-visible strings are byte-identical on any host.
/// </summary>
public static class LegacyCulture
{
    public static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    // BR-UI-015: "C2" currency under en-US, e.g. 1234.5 -> $1,234.50, -25 -> -$25.00.
    public static string Currency(decimal value) => value.ToString("C2", EnUs);

    // BR-UI-015: "0.000" ratio display, away-from-zero at the midpoint (0.4005 -> "0.401").
    public static string Ratio3(decimal value) => value.ToString("0.000", EnUs);

    // BR-UI-015: "0.00" two-decimal display (caps, rates).
    public static string Fixed2(decimal value) => value.ToString("0.00", EnUs);
}
