namespace Shortener;

public sealed class StorageUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
