namespace VodafoneRouter.Wpf;

/// <summary>
/// Creates <see cref="VodafoneRouterClient"/> instances. Injected instead of
/// the client itself because the base URL is only known at connect time.
/// </summary>
public interface IRouterClientFactory
{
    VodafoneRouterClient Create(string baseUrl);
}
