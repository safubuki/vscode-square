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
}
