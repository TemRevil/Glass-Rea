using GlassRea.Core.Look;

namespace GlassRea.App.Pages;

// The rows of each list, kept between refreshes so only the figures that moved change on screen (see LiveList).
// Templates bind to Row with Mode=OneWay; XAML can't name a generic type, hence one small class per kind of row.

public sealed class AppItem : Live<AppRow>;
public sealed class ConnItem : Live<ConnApp>;
public sealed class HostItem : Live<HostRow>;
public sealed class DeviceItem : Live<DeviceRow>;
public sealed class FirewallItem : Live<FirewallRow>;
public sealed class AlertItem : Live<AlertRow>;
