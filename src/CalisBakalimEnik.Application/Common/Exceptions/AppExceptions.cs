namespace CalisBakalimEnik.Application.Common.Exceptions;

public class NotFoundException(string message = "The requested resource was not found.")
    : Exception(message);

public class ForbiddenException(string message = "You are not allowed to perform this action.")
    : Exception(message);

public class ConflictException(string message = "The request conflicts with the current state.")
    : Exception(message);

public class UnauthorizedException(string message = "Authentication is required.")
    : Exception(message);

public class AppValidationException : Exception
{
    public AppValidationException(IDictionary<string, string[]> errors)
        : base("One or more fields are invalid.") => Errors = errors;

    public IDictionary<string, string[]> Errors { get; }
}
