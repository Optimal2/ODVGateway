namespace ODVGateway.Services.Signatures;

/// <summary>
/// A named isolated-worker failure: the worker crashed, timed out, exceeded its memory cap, or
/// answered with unparseable output. The endpoint reports these as HTTP 503 with <see cref="Code"/>
/// (<c>validation-worker-crashed</c>, <c>validation-worker-timeout</c>,
/// <c>validation-worker-memory</c>); the message is a fixed public-safe text with no stack trace,
/// no exit code, and no document content.
/// </summary>
public sealed class SignatureWorkerException : Exception
{
    public SignatureWorkerException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The stable machine code the endpoint returns next to the error text.</summary>
    public string Code { get; }

    public static SignatureWorkerException Crashed() => new(
        SignatureValidationReasons.ValidationWorkerCrashed,
        "Signature validation failed in the isolated worker process.");

    public static SignatureWorkerException TimedOut() => new(
        SignatureValidationReasons.ValidationWorkerTimeout,
        "Signature validation timed out in the isolated worker process.");

    public static SignatureWorkerException OutOfMemory() => new(
        SignatureValidationReasons.ValidationWorkerMemory,
        "Signature validation exceeded its memory limit in the isolated worker process.");
}
