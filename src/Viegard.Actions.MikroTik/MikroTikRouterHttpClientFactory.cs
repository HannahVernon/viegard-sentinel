using Viegard.Application.Configuration;

namespace Viegard.Actions.MikroTik;

public sealed class MikroTikRouterHttpClientFactory : IMikroTikRouterHttpClientFactory
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public HttpClient CreateClient(MikroTikRouter router, out RouterTransportValidationState transportState)
    {
        var handler = RouterTransportHandlerFactory.CreateHandler(router, out transportState);
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout,
        };
    }
}
