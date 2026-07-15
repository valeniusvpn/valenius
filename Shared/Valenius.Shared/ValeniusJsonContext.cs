using System.Text.Json.Serialization;

namespace Valenius.Shared;

[JsonSerializable(typeof(PipeCommand))]
[JsonSerializable(typeof(PipeResponse))]
[JsonSerializable(typeof(TunnelStatus))]
[JsonSerializable(typeof(ConnectedTunnelInfo))]
[JsonSerializable(typeof(ConnectedTunnelInfo[]))]
[JsonSerializable(typeof(ConfigInfo))]
[JsonSerializable(typeof(VersionCheckResult))]
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(RegistrationResult))]
[JsonSerializable(typeof(string[]))]
public partial class ValeniusJsonContext : JsonSerializerContext { }
