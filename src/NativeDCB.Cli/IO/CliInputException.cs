namespace NativeDCB.Cli.IO;

internal sealed class CliInputException(string message, Exception? innerException = null) :
    Exception(message, innerException);