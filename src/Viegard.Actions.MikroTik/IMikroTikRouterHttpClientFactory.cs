using Viegard.Application.Configuration;

namespace Viegard.Actions.MikroTik;

public interface IMikroTikRouterHttpClientFactory
{
    HttpClient CreateClient(MikroTikRouter router, out RouterTransportValidationState transportState);
}
