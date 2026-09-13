namespace Ticketing.Domain;

/// <summary>
/// A domain rule was violated. Maps to 422 at the edge: the request was
/// well-formed but semantically invalid.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
    public DomainException() { }
}
