namespace WiimoteLib;

public static class ConversionExtensions
{
    public static Int16 ToInt16Clamped(this Int64 value)
    {
        return (Int16)Math.Clamp(value, Int16.MinValue, Int16.MaxValue);
    }
}