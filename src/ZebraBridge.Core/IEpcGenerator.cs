namespace ZebraBridge.Core;

public interface IEpcGenerator
{
    IReadOnlyList<string> NextEpcs(int count);
}
