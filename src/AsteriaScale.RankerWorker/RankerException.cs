namespace AsteriaScale.RankerWorker;

internal sealed class RankerException : Exception
{
    public RankerException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
