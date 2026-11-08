namespace Setpoint.Core.Providers;

/// <summary>
/// Thrown by <see cref="IProviderAdapter.MapObjects"/> when the policy can't be mapped onto the repo, such as a
/// required check naming a pipeline that doesn't exist. Plan reports it as an error with its code, for that repo only.
/// </summary>
public sealed class PolicyMappingException : Exception
{
    public PolicyMappingException(string code, string message)
        : base(message) => Code = code;

    public PolicyMappingException()
        : this("SP0000", "The policy can't be mapped onto the repo.")
    {
    }

    public PolicyMappingException(string message)
        : this("SP0000", message)
    {
    }

    public PolicyMappingException(string message, Exception innerException)
        : base(message, innerException) => Code = "SP0000";

    /// <summary>The diagnostic code plan reports.</summary>
    public string Code { get; }
}
