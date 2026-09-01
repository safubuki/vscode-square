using System.Text.Json;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class VscodeWorkspaceStateTests
{
    public static void Run(List<string> failures)
    {
        const string zennContents = @"C:\git_home\zenn-contents";
        const string year2025 = @"C:\git_home\2025";
        const string hub = @"C:\git_home\turtle-ai-code-quartet-hub";
        const string shortHub = @"C:\git_home\hub";
        const string workspaceFile = @"C:\git_home\notes\2025.code-workspace";
        const string oldSshWorkspace = "vscode-remote://ssh-remote+old-host/home/developer/project";
        const string currentSshWorkspace = "vscode-remote://ssh-remote+current-host/home/developer/project";
        const string parentWorkspace = @"C:\work\project";
        const string childWorkspace = @"C:\work\project\src";

        Verify(
            failures,
            "開いているファイル名に 2025 が含まれても 2025 フォルダとは判定しない",
            () => !VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "turtle-20250502-gem.md - zenn-contents - Visual Studio Code",
                year2025));

        Verify(
            failures,
            "同じタイトルでも zenn-contents はワークスペースとして判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "turtle-20250502-gem.md - zenn-contents - Visual Studio Code",
                zennContents));

        Verify(
            failures,
            "2025 フォルダを開いているタイトルは 2025 と判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "readme.md - 2025 - Visual Studio Code",
                year2025));

        Verify(
            failures,
            "未保存マーク付きでも zenn-contents を判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "● turtle-20250502-gem.md - zenn-contents - Visual Studio Code",
                zennContents));

        Verify(
            failures,
            "SSH リモートのタイトル接尾辞でもワークスペース名を判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "file.md - zenn-contents [SSH: host] - Visual Studio Code",
                zennContents));

        Verify(
            failures,
            "SSH 接続名が異なる古いリモート URI は現在のウィンドウと判定しない",
            () => !VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "file.md - project [SSH: current-host] - Visual Studio Code",
                oldSshWorkspace));

        Verify(
            failures,
            "SSH 接続名が一致する現在のリモート URI を判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "file.md - project [SSH: current-host] - Visual Studio Code",
                currentSshWorkspace));

        Verify(
            failures,
            "同じフォルダ名の SSH 候補では古い候補順より現在の接続名を優先する",
            () => string.Equals(
                VscodeWorkspaceState.TryPickWorkspacePathVisibleInWindowTitle(
                    "file.md - project [SSH: current-host] - Visual Studio Code",
                    [oldSshWorkspace, currentSshWorkspace]),
                currentSshWorkspace,
                StringComparison.OrdinalIgnoreCase));

        Verify(
            failures,
            "SSH 接続名を表示しないカスタムタイトルではフォルダ名照合を維持する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "file.md - project - Visual Studio Code",
                currentSshWorkspace));

        Verify(
            failures,
            "親フォルダを開いた場合は子フォルダ候補ではなく親を選ぶ",
            () => string.Equals(
                VscodeWorkspaceState.TryPickWorkspacePathVisibleInWindowTitle(
                    "Program.cs - project - Visual Studio Code",
                    [childWorkspace, parentWorkspace]),
                parentWorkspace,
                StringComparison.OrdinalIgnoreCase));

        Verify(
            failures,
            "一階層深い子フォルダを開いた場合は実際の子フォルダを選ぶ",
            () => string.Equals(
                VscodeWorkspaceState.TryPickWorkspacePathVisibleInWindowTitle(
                    "Program.cs - src - Visual Studio Code",
                    [parentWorkspace, childWorkspace]),
                childWorkspace,
                StringComparison.OrdinalIgnoreCase));

        Verify(
            failures,
            "同名ローカルフォルダは workspace.json の作成順ではなく最新利用状態で選ぶ",
            VerifyLatestWorkspaceActivityWinsEvenWithCachedCandidates);

        Verify(
            failures,
            "フォルダ名だけのタイトルでも判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "zenn-contents - Visual Studio Code",
                zennContents));

        Verify(
            failures,
            "長いワークスペース名の一部である hub にはマッチしない",
            () => !VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "Program.cs - turtle-ai-code-quartet-hub - Visual Studio Code",
                shortHub));

        Verify(
            failures,
            "長いワークスペース名そのものにはマッチする",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "Program.cs - turtle-ai-code-quartet-hub - Visual Studio Code",
                hub));

        Verify(
            failures,
            "code-workspace ファイル名でも判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "notes.md - 2025 - Visual Studio Code",
                workspaceFile));

        Verify(
            failures,
            "code-workspace 名の 2025 もファイル名の部分数字ではマッチしない",
            () => !VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "turtle-20250502-gem.md - zenn-contents - Visual Studio Code",
                workspaceFile));

        Verify(
            failures,
            "ファイル名の 2025 より実際の zenn-contents を保存対象に選ぶ",
            () => string.Equals(
                VscodeWorkspaceState.TryPickWorkspacePathVisibleInWindowTitle(
                    "turtle-20250502-gem.md - zenn-contents - Visual Studio Code",
                    [year2025, zennContents]),
                zennContents,
                StringComparison.OrdinalIgnoreCase));

        Verify(
            failures,
            "候補順が逆でも zenn-contents を優先する",
            () => string.Equals(
                VscodeWorkspaceState.TryPickWorkspacePathVisibleInWindowTitle(
                    "turtle-20250502-gem.md - zenn-contents - Visual Studio Code",
                    [zennContents, year2025]),
                zennContents,
                StringComparison.OrdinalIgnoreCase));

        Verify(
            failures,
            "空タイトルではワークスペースを選ばない",
            () => VscodeWorkspaceState.TryPickWorkspacePathVisibleInWindowTitle(
                "   ",
                [year2025, zennContents]) is null);

        Verify(
            failures,
            "Antigravity のタイトルでもワークスペース名を判定する",
            () => VscodeWorkspaceState.IsWorkspaceVisibleInWindowTitle(
                "SKILL.md - zenn-contents - Antigravity",
                zennContents));
    }

    private static void Verify(List<string> failures, string name, Func<bool> assertion)
    {
        try
        {
            if (!assertion())
            {
                failures.Add(name);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    private static bool VerifyLatestWorkspaceActivityWinsEvenWithCachedCandidates()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "TurtleAIQuartetHub.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var config = new AppConfig
            {
                StateDirectory = testRoot,
                UseDedicatedUserDataDirs = true
            };
            var storageRoot = Path.Combine(testRoot, "user-data", "A", "User", "workspaceStorage");
            var firstPath = Path.Combine(testRoot, "first", "project");
            var secondPath = Path.Combine(testRoot, "second", "project");
            var now = DateTime.UtcNow;

            var firstDatabase = CreateWorkspaceCandidate(
                storageRoot,
                "first-candidate",
                firstPath,
                workspaceJsonTimeUtc: now.AddDays(-30),
                stateDatabaseTimeUtc: now.AddMinutes(-1));
            var secondDatabase = CreateWorkspaceCandidate(
                storageRoot,
                "second-candidate",
                secondPath,
                workspaceJsonTimeUtc: now.AddDays(-1),
                stateDatabaseTimeUtc: now.AddHours(-1));

            var firstSelection = VscodeWorkspaceState.TryReadCurrentWorkspacePath(
                AppConfig.VsCodeApplicationId,
                "A",
                "Program.cs - project - Visual Studio Code",
                config);
            if (!string.Equals(firstSelection, firstPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // workspaceStorage 直下や workspace.json を変更せず利用状態だけを反転する。
            // スキャンキャッシュ中でも state.vscdb の最新時刻を再確認できることを検証する。
            File.SetLastWriteTimeUtc(firstDatabase, now.AddHours(-2));
            File.SetLastWriteTimeUtc(secondDatabase, now);

            var secondSelection = VscodeWorkspaceState.TryReadCurrentWorkspacePath(
                AppConfig.VsCodeApplicationId,
                "A",
                "Program.cs - project - Visual Studio Code",
                config);
            return string.Equals(secondSelection, secondPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static string CreateWorkspaceCandidate(
        string storageRoot,
        string candidateId,
        string workspacePath,
        DateTime workspaceJsonTimeUtc,
        DateTime stateDatabaseTimeUtc)
    {
        var candidateDirectory = Path.Combine(storageRoot, candidateId);
        Directory.CreateDirectory(candidateDirectory);

        var workspaceJsonPath = Path.Combine(candidateDirectory, "workspace.json");
        File.WriteAllText(
            workspaceJsonPath,
            JsonSerializer.Serialize(new { folder = new Uri(workspacePath).AbsoluteUri }));
        File.SetLastWriteTimeUtc(workspaceJsonPath, workspaceJsonTimeUtc);

        var stateDatabasePath = Path.Combine(candidateDirectory, "state.vscdb");
        File.WriteAllText(stateDatabasePath, string.Empty);
        File.SetLastWriteTimeUtc(stateDatabasePath, stateDatabaseTimeUtc);
        return stateDatabasePath;
    }
}
