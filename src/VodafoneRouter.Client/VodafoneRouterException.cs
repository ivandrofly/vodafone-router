namespace VodafoneRouter;

public class VodafoneRouterException : Exception
{
    public VodafoneRouterException(string message) : base(message)
    {
    }

    public VodafoneRouterException(string message, Exception inner) : base(message, inner)
    {
    }
}

public class VodafoneRouterAuthException : VodafoneRouterException
{
    public VodafoneRouterAuthException(string message) : base(message)
    {
    }
}