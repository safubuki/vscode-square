using System.Diagnostics;
using System.Windows;
using System.Windows.Shell;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class TaskbarJumpListService
{
    private static string? _lastSignature;

    public static void Update(IReadOnlyList<WindowSlot> slots, bool compactMode)
    {
        var appPath = GetCurrentAppPath();
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        var managedSlots = slots.Take(4).ToList();
        var visibleSlots = managedSlots
            .Where(slot => slot.WindowStatus != SlotWindowStatus.Missing)
            .ToList();
        var allSlotsStopped = managedSlots.Count > 0
            && managedSlots.All(slot => slot.WindowStatus == SlotWindowStatus.Missing);

        var signature = BuildSignature(managedSlots, compactMode, isActiveMenu: true);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        var jumpList = CreateBaseJumpList();

        if (visibleSlots.Count > 0)
        {
            foreach (var slot in visibleSlots)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = BuildSlotTitle(slot),
                    Description = $"スロット {slot.Name} の VS Code を切り替えます。",
                    Arguments = $"--slot-toggle {slot.Name}",
                    ApplicationPath = appPath,
                    IconResourcePath = appPath,
                    CustomCategory = "スロット"
                });
            }
        }
        else if (allSlotsStopped)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "VS Code を一括起動",
                Description = "管理する 4 スロットの VS Code をまとめて起動します。",
                Arguments = "--launch-all",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "起動"
            });
        }

        jumpList.JumpItems.Add(new JumpTask
        {
            Title = compactMode ? "標準表示に戻す" : "縮小表示にする",
            Description = "パネルの表示モードを切り替えます。",
            Arguments = compactMode ? "--mode standard" : "--mode compact",
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "表示"
        });

        if (compactMode)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "アプリを探す",
                Description = "縮小表示のパネル位置を点滅で知らせます。",
                Arguments = "--locate",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "表示"
            });
        }

        Apply(jumpList, signature);
    }

    public static void SetInactiveMenu()
    {
        var appPath = GetCurrentAppPath();
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        var signature = BuildSignature([], compactMode: false, isActiveMenu: false);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        var jumpList = CreateBaseJumpList();
        jumpList.JumpItems.Add(new JumpTask
        {
            Title = "Turtle AI Quartet Hub を起動",
            Description = "アプリが起動していないときに Turtle AI Quartet Hub を開きます。",
            Arguments = "--activate",
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "起動"
        });

        Apply(jumpList, signature);
    }

    private static JumpList CreateBaseJumpList()
    {
        return new JumpList
        {
            ShowRecentCategory = false,
            ShowFrequentCategory = false
        };
    }

    private static string? GetCurrentAppPath()
    {
        if (Application.Current is null)
        {
            return null;
        }

        return Process.GetCurrentProcess().MainModule?.FileName;
    }

    private static void Apply(JumpList jumpList, string signature)
    {
        if (Application.Current is null)
        {
            return;
        }

        try
        {
            JumpList.SetJumpList(Application.Current, jumpList);
            jumpList.Apply();
            _lastSignature = signature;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private static string BuildSignature(IEnumerable<WindowSlot> slots, bool compactMode, bool isActiveMenu)
    {
        var slotSignature = string.Join(
            "|",
            slots.Select(slot => $"{slot.Name}:{slot.WindowStatus}:{slot.AiStatus}:{slot.DisplayTitle}"));
        return $"{isActiveMenu}:{compactMode}:{slotSignature}";
    }

    private static string BuildSlotTitle(WindowSlot slot)
    {
        var title = slot.DisplayTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"スロット {slot.Name}";
        }

        if (title.Length > 18)
        {
            title = $"{title[..17]}…";
        }

        var status = slot.AiStatus switch
        {
            AiStatus.Running => "実行中",
            AiStatus.Completed => "完了",
            AiStatus.WaitingForConfirmation => "確認中",
            AiStatus.Error => "エラー",
            AiStatus.NeedsAttention => "要対応",
            _ => slot.WindowStatus == SlotWindowStatus.Launching ? "起動中" : "待機"
        };

        return $"{slot.Name} {title} [{status}]";
    }
}
