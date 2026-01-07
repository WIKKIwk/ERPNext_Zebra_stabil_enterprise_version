namespace ZebraBridge.Core;

public class ZebraBridgeException : Exception
{
    public ZebraBridgeException(string message) : base(message)
    {
    }

    public ZebraBridgeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public class InvalidEpcException : ZebraBridgeException
{
    public InvalidEpcException(string message) : base(message)
    {
    }
}

public class PrinterNotFoundException : ZebraBridgeException
{
    public PrinterNotFoundException(string message) : base(message)
    {
    }
}

public class PrinterCommunicationException : ZebraBridgeException
{
    public PrinterCommunicationException(string message, Exception? innerException = null)
        : base(message, innerException ?? new Exception(message))
    {
    }
}

public class PrinterUnsupportedOperationException : ZebraBridgeException
{
    public PrinterUnsupportedOperationException(string message) : base(message)
    {
    }
}

public class EpcGeneratorException : ZebraBridgeException
{
    public EpcGeneratorException(string message) : base(message)
    {
    }
}
