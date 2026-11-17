using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SldFile;

namespace SldFile.CleanUsers;

/// <summary>
/// Removes recorded user names and author metadata (Author, LastSavedBy) from
/// SolidWorks part, assembly and drawing files.
///
/// The names live in many streams at once (Header2, Config-0,
/// Config-0-ModelHeader, CMgr, Definition, DisplayLists, VisualStates, Biography).
/// Clearing only Header2 is not enough: SolidWorks rebuilds it from the leftover
/// copies the next time the document is saved.
///
/// Usage:
///   SldFile.CleanUsers &lt;file-or-folder&gt; [options]
/// Options:
///   --purge                Delete the user list entirely (count = 0, list reads empty)
///   --replacement &lt;name&gt;  Placeholder user name (default: "user", placeholder mode only)
///   --dry-run              Show what would change without saving
///   --no-backup            Skip the .bak backup copy
///   --recursive            Search folder recursively
/// </summary>
internal static class Program
{
    private static readonly string[] Extensions = { ".sldprt", ".sldasm", ".slddrw" };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? path = null;
        string replacement = "user";
        bool dryRun = false, noBackup = false, recursive = false, purge = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--purge":
                    purge = true;
                    break;
                case "--replacement" when i + 1 < args.Length:
                    replacement = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-backup":
                    noBackup = true;
                    break;
                case "--recursive":
                case "-r":
                    recursive = true;
                    break;
                case "-h" or "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (path == null) path = args[i];
                    else { Console.Error.WriteLine($"无法识别的参数: {args[i]}"); PrintUsage(); return 2; }
                    break;
            }
        }

        if (path == null || !File.Exists(path) && !Directory.Exists(path))
        {
            Console.Error.WriteLine("请提供有效的 SolidWorks 文件或文件夹路径。");
            PrintUsage();
            return 2;
        }

        List<string> files;
        if (File.Exists(path))
        {
            files = new List<string> { Path.GetFullPath(path) };
        }
        else
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files = Extensions.SelectMany(ext => Directory.EnumerateFiles(path, "*" + ext, option))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (files.Count == 0)
        {
            Console.WriteLine("未找到 .sldprt / .sldasm / .slddrw 文件。");
            return 0;
        }

        if (dryRun)
            Console.WriteLine("== 预演模式：不会修改任何文件 ==\n");

        int cleaned = 0, skipped = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                if (ProcessFile(file, replacement, dryRun, noBackup, purge))
                    cleaned++;
                else
                    skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[失败] {file}\n        {ex.Message}");
            }
        }

        Console.WriteLine($"\n完成: {cleaned} 个已清理, {skipped} 个无变化, {failed} 个失败。");
        return failed == 0 ? 0 : 1;
    }

    private static bool ProcessFile(string filePath, string replacement, bool dryRun, bool noBackup, bool purge)
    {
        Console.WriteLine($"── {filePath}");

        List<string> beforeUsers;
        string? beforeAuthor, beforeLastSavedBy;
        using (var probe = SwFile.Open(filePath, readOnly: true))
        {
            beforeUsers = probe.Users.ToList();
            beforeAuthor = probe.Metadata.Author;
            beforeLastSavedBy = probe.Metadata.LastSavedBy;
            Console.WriteLine($"   清理前  用户: [{Display(beforeUsers)}]  作者: {beforeAuthor ?? "(空)"}  最后保存者: {beforeLastSavedBy ?? "(空)"}");
        }

        bool hasUsers = beforeUsers.Any(u => !string.IsNullOrWhiteSpace(u));
        bool hasAuthor = !string.IsNullOrWhiteSpace(beforeAuthor) || !string.IsNullOrWhiteSpace(beforeLastSavedBy);
        if (!hasUsers && !hasAuthor)
        {
            Console.WriteLine("   无需清理，跳过。");
            return false;
        }

        if (dryRun)
        {
            Console.WriteLine(purge
                ? "   （预演）将彻底删除用户名条目并清空作者/最后保存者。"
                : $"   （预演）将把用户名替换为占位符 \"{replacement}\" 并清空作者/最后保存者。");
            return true;
        }

        if (!noBackup)
        {
            string backup = filePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(filePath, backup, overwrite: false);
            Console.WriteLine($"   备份: {backup}");
        }

        List<string> removedNames;
        using (var doc = SwFile.Open(filePath, readOnly: false))
        {
            doc.Metadata.Author = "";
            doc.Metadata.LastSavedBy = "";
            removedNames = (purge ? doc.ClearUsers() : doc.SanitizeUsers(replacement)).ToList();
            doc.Save();
        }

        // Verify by reopening in read-only mode.
        using (var verify = SwFile.Open(filePath, readOnly: true))
        {
            Console.WriteLine($"   清理后  用户: [{Display(verify.Users)}]  作者: {verify.Metadata.Author ?? "(空)"}  最后保存者: {verify.Metadata.LastSavedBy ?? "(空)"}");
            if (removedNames.Count > 0)
                Console.WriteLine($"   已清除 {removedNames.Count} 个名字: {string.Join(", ", removedNames)}");
        }

        return true;
    }

    private static string Display(IReadOnlyList<string> users)
    {
        if (users.Count == 0) return string.Empty;
        if (users.All(string.IsNullOrEmpty)) return $"（{users.Count} 个空条目，无名字）";
        return string.Join(", ", users.Select(u => string.IsNullOrEmpty(u) ? "—" : u));
    }

    private static bool IsAlreadyPlaceholder(string name, string replacement) =>
        name.All(c => replacement.Contains(c));

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SldFile.CleanUsers — 清除 SolidWorks 文件中的用户名与作者信息

            用法:
              SldFile.CleanUsers <文件或文件夹> [选项]

            选项:
              --purge                彻底删除用户列表（清空为 0 条），不留任何占位符
              --replacement <名称>   占位用户名 (默认: "user"，仅非 purge 模式)
              --dry-run              仅预览，不修改文件
              --no-backup            不生成 .bak 备份
              --recursive, -r        递归搜索子文件夹
            """);
    }
}
