using TurtleAIQuartetHub.Panel.Services;

var failures = new List<string>();

await VerifyAsync(
    "既に閉じている HWND は成功し、close を送らない",
    initiallyLive: false,
    requestAccepted: true,
    pollsUntilClosed: null,
    ManagedWindowCloseStatus.AlreadyClosed,
    expectedCloseRequests: 0);

await VerifyAsync(
    "WM_CLOSE 拒否時は管理解除しない結果を返す",
    initiallyLive: true,
    requestAccepted: false,
    pollsUntilClosed: null,
    ManagedWindowCloseStatus.RequestFailed,
    expectedCloseRequests: 1);

await VerifyAsync(
    "遅れて閉じるウィンドウは HWND 消滅後にだけ成功する",
    initiallyLive: true,
    requestAccepted: true,
    pollsUntilClosed: 3,
    ManagedWindowCloseStatus.Closed,
    expectedCloseRequests: 1);

await VerifyAsync(
    "待機期限ちょうどに閉じた HWND も終了済みと判定する",
    initiallyLive: true,
    requestAccepted: true,
    pollsUntilClosed: 4,
    ManagedWindowCloseStatus.Closed,
    expectedCloseRequests: 1);

await VerifyAsync(
    "閉じずに残るウィンドウはタイムアウトになり成功扱いしない",
    initiallyLive: true,
    requestAccepted: true,
    pollsUntilClosed: null,
    ManagedWindowCloseStatus.TimedOut,
    expectedCloseRequests: 1);

VscodeWorkspaceStateTests.Run(failures);
VscodeUserSettingsTests.Run(failures);

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("Managed window close regression checks passed (5/5).");
Console.WriteLine("VS Code workspace title matching regression checks passed (21/21).");
Console.WriteLine("VS Code shared user settings regression checks passed (5/5).");
return 0;

async Task VerifyAsync(
    string name,
    bool initiallyLive,
    bool requestAccepted,
    int? pollsUntilClosed,
    ManagedWindowCloseStatus expectedStatus,
    int expectedCloseRequests)
{
    var live = initiallyLive;
    var polls = 0;
    var closeRequests = 0;
    var service = new ManagedWindowCloseService(
        _ => live,
        _ =>
        {
            closeRequests++;
            return requestAccepted;
        },
        (_, _) =>
        {
            polls++;
            if (pollsUntilClosed.HasValue && polls >= pollsUntilClosed.Value)
            {
                live = false;
            }

            return Task.CompletedTask;
        },
        timeout: TimeSpan.FromMilliseconds(400),
        pollInterval: TimeSpan.FromMilliseconds(100));

    var result = await service.CloseAndWaitAsync(new IntPtr(42));
    if (result.Status != expectedStatus)
    {
        failures.Add($"{name}: expected={expectedStatus}, actual={result.Status}");
    }

    if (closeRequests != expectedCloseRequests)
    {
        failures.Add($"{name}: close requests expected={expectedCloseRequests}, actual={closeRequests}");
    }
}
