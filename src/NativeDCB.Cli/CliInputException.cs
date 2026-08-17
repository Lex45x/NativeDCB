namespace NativeDCB.Cli;

internal sealed class CliInputException(string message, Exception? innerException = null) :
    Exception(message, innerException);