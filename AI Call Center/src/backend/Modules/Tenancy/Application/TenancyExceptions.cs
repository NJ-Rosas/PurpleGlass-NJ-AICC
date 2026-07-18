namespace PurpleGlass.Modules.Tenancy.Application;

public sealed class TenancyResourceNotFoundException : Exception
{
    public TenancyResourceNotFoundException()
        : base("The requested tenant resource was not found.")
    {
    }
}

public sealed class TenancyConcurrencyException : Exception
{
    public TenancyConcurrencyException()
        : base("The location was changed by another request.")
    {
    }

    public TenancyConcurrencyException(Exception innerException)
        : base("The location was changed by another request.", innerException)
    {
    }
}
