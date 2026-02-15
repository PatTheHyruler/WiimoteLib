namespace WiimoteLib;

public class WiimoteReadDataException(
    DataReadErrorType errorType,
    string message,
    byte[] failureReasonBuffer)
    : WiimoteException(message)
{
    public DataReadErrorType ErrorType { get; } = errorType;
    public byte[] FailureReasonBuffer { get; } = failureReasonBuffer;
}

public enum DataReadErrorType
{
    ReadFromWriteOnlyRegister = 7,
    NonExistentMemoryAddress = 8,
}