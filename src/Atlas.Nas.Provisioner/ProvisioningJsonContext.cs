using System.Text.Json.Serialization;
using Miguel.Contracts;

namespace Atlas.Nas.Provisioner;

[JsonSerializable(typeof(NasProvisioningRequest))]
[JsonSerializable(typeof(NasProvisioningResponse))]
internal sealed partial class ProvisioningJsonContext : JsonSerializerContext;
