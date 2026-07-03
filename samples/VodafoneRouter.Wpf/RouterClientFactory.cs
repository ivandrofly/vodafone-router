namespace VodafoneRouter.Wpf;

public sealed class RouterClientFactory : IRouterClientFactory
{
    public VodafoneRouterClient Create(string baseUrl)
    {
        return new VodafoneRouterClient(baseUrl);
    }
}
