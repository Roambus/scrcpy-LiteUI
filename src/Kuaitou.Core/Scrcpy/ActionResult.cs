namespace Kuaitou.Core.Scrcpy;

/// <summary>一次设备操作的成败与给用户看的说明。对应旧版各处返回的 {ok, msg}。</summary>
public sealed record ActionResult(bool Ok, string Message)
{
    public static ActionResult Success(string message) => new(true, message);

    public static ActionResult Failure(string message) => new(false, message);
}
